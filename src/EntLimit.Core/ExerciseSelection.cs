namespace EntLimit.Core;

public sealed class ExerciseProgress
{
    public bool Passed { get; init; }
    public int PassedCount { get; init; }
    public int ReviewCount { get; init; }
    public bool SourcePassed { get; init; }
    public DateTimeOffset? LastPassedAt { get; init; }
    public DateTimeOffset? LastReviewedAt { get; init; }
    public DateTimeOffset? LastPracticedAt { get; init; }
}

/// <summary>Derived exclusively from successful learning history; no separate mutable progress state.</summary>
public static class ExerciseSelection
{
    /// <summary>Returns only the highest-priority available pool, ranked oldest first, then fewer passes, then ID.
    /// Callers may skip the current item within this pool, but must not append lower-priority pools.</summary>
    public static IReadOnlyList<Exercise> Rank(IEnumerable<Exercise> exercises, IEnumerable<LearningRecord> history,
        string topicId, string mode)
    {
        var all = exercises.ToList();
        var records = SuccessfulRecords(history);
        var topic = all.Where(e => topicId == "all" || e.TopicId == topicId).ToList();
        var originals = topic.Where(e => e.ReviewOfId is null).ToList();
        var passedIds = records.Select(r => r.QuestionId).ToHashSet(StringComparer.Ordinal);
        var reviews = topic.Where(e => e.ReviewOfId is not null && originals.Any(o => o.Id == e.ReviewOfId && o.TopicId == e.TopicId)).ToList();
        List<Exercise> pool;
        if (mode == "light")
        {
            pool = reviews.Where(e => passedIds.Contains(e.ReviewOfId!)).ToList();
            if (pool.Count > 0)
                return pool.OrderBy(e => Latest(records.Where(r => r.QuestionId == e.Id || r.QuestionId == e.ReviewOfId)))
                    .ThenBy(e => records.Count(r => r.QuestionId == e.Id)).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
            pool = originals.Where(e => e.Mode == "light" && !passedIds.Contains(e.Id)).ToList();
            if (pool.Count == 0) pool = originals.Where(e => !passedIds.Contains(e.Id)).ToList();
            if (pool.Count == 0) pool = originals;
        }
        else
        {
            pool = originals.Where(e => e.Mode == "standard" && !passedIds.Contains(e.Id)).ToList();
            if (pool.Count == 0) pool = originals.Where(e => e.Mode == "light" && !passedIds.Contains(e.Id)).ToList();
            if (pool.Count == 0) pool = originals;
        }
        bool completedPool = pool.All(e => passedIds.Contains(e.Id));
        return pool.OrderBy(e => Latest(records.Where(r => r.QuestionId == e.Id
                || completedPool && reviews.Any(review => review.Id == r.QuestionId && review.ReviewOfId == e.Id))))
            .ThenBy(e => records.Count(r => r.QuestionId == e.Id)).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Selects from the highest-priority pool only. Avoiding the current exercise never
    /// lowers its priority tier: a single eligible exercise is returned again rather than skipped.</summary>
    public static Exercise? Pick(IEnumerable<Exercise> exercises, IEnumerable<LearningRecord> history,
        string topicId, string mode, bool random = false, string? avoidExerciseId = null, Random? rng = null)
    {
        var ranked = Rank(exercises, history, topicId, mode);
        if (ranked.Count == 0) return null;
        var candidates = ranked.Count > 1 && avoidExerciseId is not null
            ? ranked.Where(e => e.Id != avoidExerciseId).ToList()
            : ranked;
        return candidates[random ? (rng ?? Random.Shared).Next(candidates.Count) : 0];
    }

    public static ExerciseProgress Progress(Exercise exercise, IEnumerable<LearningRecord> history,
        IEnumerable<Exercise>? exercises = null)
    {
        var records = SuccessfulRecords(history);
        var direct = records.Where(r => r.QuestionId == exercise.Id).ToList();
        var reviewIds = exercise.ReviewOfId is null
            ? (exercises ?? ExerciseCatalog.All).Where(e => e.ReviewOfId == exercise.Id).Select(e => e.Id).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal) { exercise.Id };
        var reviews = records.Where(r => reviewIds.Contains(r.QuestionId)).ToList();
        return new ExerciseProgress
        {
            Passed = direct.Count > 0, PassedCount = direct.Count, ReviewCount = reviews.Count,
            SourcePassed = exercise.ReviewOfId is null ? direct.Count > 0 : records.Any(r => r.QuestionId == exercise.ReviewOfId),
            LastPassedAt = Latest(direct), LastReviewedAt = Latest(reviews), LastPracticedAt = Latest(direct.Concat(reviews))
        };
    }

    // Monitor only stores completed submissions. Empty attempt IDs are not completion evidence;
    // retried identical requests must never inflate counts, including malformed duplicated history.
    private static List<LearningRecord> SuccessfulRecords(IEnumerable<LearningRecord> history) => history
        .Where(r => !string.IsNullOrWhiteSpace(r.AttemptId) && !string.IsNullOrWhiteSpace(r.QuestionId))
        .GroupBy(r => r.AttemptId, StringComparer.Ordinal).Select(g => g.First()).ToList();
    private static DateTimeOffset? Latest(IEnumerable<LearningRecord> records) => records.Select(r => (DateTimeOffset?)r.CompletedAtUtc).Max();
}
