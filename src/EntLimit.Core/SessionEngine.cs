namespace EntLimit.Core;

/// <summary>Pure cooperative permit logic. Call only from the monitor's single mutation thread.</summary>
public sealed class SessionEngine
{
    public SessionState State { get; }
    public int WarningMinutes { get; set; } = 10;

    public SessionEngine(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state;
        State.ActiveProcesses ??= [];
        State.DrainingProcesses ??= [];
        State.History ??= [];
        if (!Enum.IsDefined(State.Phase)) throw new ArgumentException("Unknown session phase.", nameof(state));
        if (State.Phase == SessionPhase.Active && State.DeadlineUtc is null)
            throw new ArgumentException("Active session is missing its deadline.", nameof(state));
        if (State.GrantedMinutes < 90) throw new ArgumentException("Permit duration must be at least 90 minutes.", nameof(state));
    }

    public bool Complete(Question question, int answerIndex, string attemptId, DateTimeOffset now, int unlockMinutes)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (string.IsNullOrWhiteSpace(attemptId) || attemptId.Length > 128)
            throw new ArgumentException("A bounded attempt ID is required.", nameof(attemptId));
        // A retried successful request is acknowledged, even if entertainment has started.
        var previous = State.History.FirstOrDefault(x => x.AttemptId == attemptId);
        if (previous is not null)
            return previous.QuestionId == question.Id && answerIndex == question.CorrectIndex;
        if (State.Phase != SessionPhase.Locked) return false;
        if (answerIndex < 0 || answerIndex >= question.Options.Length || answerIndex != question.CorrectIndex)
            return false;
        return Commit(question.Id, question.TopicId, question.Mode, question.Takeaway, attemptId, now, unlockMinutes);
    }

    /// <summary>Monitor only: call after grading all parts or successfully executing every submitted code case.</summary>
    public bool CompleteValidated(Exercise exercise, string attemptId, DateTimeOffset now, int unlockMinutes)
    {
        ArgumentNullException.ThrowIfNull(exercise);
        if (string.IsNullOrWhiteSpace(exercise.Id) || string.IsNullOrWhiteSpace(exercise.TopicId)
            || exercise.Mode is not ("standard" or "light") || exercise.Kind is not ("scenario" or "code"))
            throw new ArgumentException("A valid exercise is required.", nameof(exercise));
        if (string.IsNullOrWhiteSpace(attemptId) || attemptId.Length > 128)
            throw new ArgumentException("A bounded attempt ID is required.", nameof(attemptId));
        var previous = State.History.FirstOrDefault(x => x.AttemptId == attemptId);
        if (previous is not null) return previous.QuestionId == exercise.Id;
        if (State.Phase != SessionPhase.Locked) return false;
        return Commit(exercise.Id, exercise.TopicId, exercise.Mode, exercise.Takeaway, attemptId, now, unlockMinutes);
    }

    private bool Commit(string exerciseId, string topicId, string mode, string takeaway,
        string attemptId, DateTimeOffset now, int unlockMinutes)
    {
        if (unlockMinutes < 90 || unlockMinutes > 24 * 60)
            throw new ArgumentOutOfRangeException(nameof(unlockMinutes), "Choose 90 to 1440 minutes.");
        now = ObserveTime(now);
        State.History.Add(new LearningRecord
        {
            AttemptId = attemptId, QuestionId = exerciseId, TopicId = topicId,
            Mode = mode, Takeaway = takeaway, CompletedAtUtc = now
        });
        State.GrantedMinutes = unlockMinutes;
        State.GrantedAtUtc = now;
        State.StartedAtUtc = State.DeadlineUtc = null;
        State.WarningDelivered = State.ExpiryDelivered = false;
        State.ActiveProcesses.Clear();
        State.DrainingProcesses.Clear();
        State.Phase = SessionPhase.Ready;
        return true;
    }
    public bool CanUse(ProcessIdentity process, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(process);
        Tick(now);
        return State.Phase is SessionPhase.Ready or SessionPhase.Active
            || State.Phase == SessionPhase.Draining && State.DrainingProcesses.Contains(process);
    }

    public bool ObserveUse(ProcessIdentity process, DateTimeOffset now)
    {
        if (!CanUse(process, now)) return false;
        now = ObserveTime(now);
        if (State.Phase == SessionPhase.Ready) Start(now);
        if (State.Phase == SessionPhase.Active && !State.ActiveProcesses.Contains(process))
            State.ActiveProcesses.Add(process);
        return true;
    }

    public bool ActivateWebsite(DateTimeOffset now)
    {
        Tick(now);
        if (State.Phase == SessionPhase.Ready) Start(ObserveTime(now));
        return State.Phase == SessionPhase.Active;
    }

    public void Reconcile(IReadOnlyCollection<ProcessIdentity> alive, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(alive);
        // Reconcile before Tick: an already exited game must not inherit grace.
        State.ActiveProcesses.RemoveAll(x => !alive.Contains(x));
        State.DrainingProcesses.RemoveAll(x => !alive.Contains(x));
        Tick(now);
        if (State.Phase == SessionPhase.Draining && State.DrainingProcesses.Count == 0)
            Finish();
    }

    public TickResult Tick(DateTimeOffset now)
    {
        var oldObserved = State.LastObservedUtc;
        now = ObserveTime(now);
        var changed = oldObserved != State.LastObservedUtc;
        if (State.Phase != SessionPhase.Active || State.DeadlineUtc is not { } deadline)
            return new(false, false, changed);
        if (now >= deadline)
        {
            var emitExpiry = !State.ExpiryDelivered;
            State.ExpiryDelivered = true;
            State.WarningDelivered = true;
            State.DrainingProcesses = State.ActiveProcesses.Distinct().ToList();
            if (State.DrainingProcesses.Count == 0) Finish();
            else State.Phase = SessionPhase.Draining;
            return new(false, emitExpiry, true);
        }
        if (!State.WarningDelivered && WarningMinutes > 0 && deadline - now <= TimeSpan.FromMinutes(WarningMinutes))
        {
            State.WarningDelivered = true;
            return new(true, false, true);
        }
        return new(false, false, changed);
    }

    public void Finish()
    {
        State.Phase = SessionPhase.Locked;
        State.ActiveProcesses.Clear();
        State.DrainingProcesses.Clear();
        State.GrantedAtUtc = State.StartedAtUtc = State.DeadlineUtc = null;
        // Preserve the delivered flags until a new grant, so an expiry is not replayed.
    }

    private void Start(DateTimeOffset now)
    {
        State.StartedAtUtc = now;
        State.DeadlineUtc = now.AddMinutes(State.GrantedMinutes);
        State.Phase = SessionPhase.Active;
    }

    private DateTimeOffset ObserveTime(DateTimeOffset now)
    {
        now = now.ToUniversalTime();
        if (now < State.LastObservedUtc) return State.LastObservedUtc;
        State.LastObservedUtc = now;
        return now;
    }
}
