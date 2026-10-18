using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EntLimit.Core;

namespace EntLimit.Desktop;

// Reading/copying this view never edits an exercise draft or invokes the monitor.
internal static class ReferenceSolutionView
{
    public static Expander Create(CodingExercise coding, Action<string> notify)
    {
        var expander = new Expander
        {
            Header = "查看参考解与优化思路",
            IsExpanded = false,
            Margin = new Thickness(0,18,0,8),
            Padding = new Thickness(0,8,0,0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            FontSize = 14,
            ToolTip = "可以在通过后对照，也可以在卡住时主动查看；不会替换你的代码。"
        };
        // Only instantiate the solution after the user explicitly opens it.
        expander.Expanded += (_,_) =>
        {
            if (expander.Content is not null) return;
            var content = new StackPanel { Margin = new Thickness(0,12,0,0) };
            content.Children.Add(Paragraph("先理解思路，再对照自己的实现。查看与复制不会改动草稿，也不会自动解锁。"));
            AddSection(content, "为什么这样写", coding.SolutionExplanation);
            content.Children.Add(Heading("参考实现"));
            AddCode(content, coding.ReferenceCode, notify);
            AddSection(content, "复杂度与适用条件", coding.Complexity);
            if (!string.IsNullOrWhiteSpace(coding.AlternativeCode))
            {
                content.Children.Add(Heading("另一种写法"));
                AddCode(content, coding.AlternativeCode, notify);
                if (!string.IsNullOrWhiteSpace(coding.AlternativeExplanation)) content.Children.Add(Paragraph(coding.AlternativeExplanation));
            }
            expander.Content = content;
        };
        return expander;
    }

    private static void AddSection(Panel parent, string title, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        parent.Children.Add(Heading(title)); parent.Children.Add(Paragraph(text));
    }
    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,18,0,9) };
    private static TextBlock Paragraph(string text) => new() { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 23, Margin = new Thickness(0,0,0,8) };
    private static void AddCode(Panel parent, string code, Action<string> notify)
    {
        parent.Children.Add(new TextBox
        {
            Text = code, IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true,
            FontFamily = new FontFamily("Consolas"), FontSize = 13,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 55, MaxHeight = 260, Margin = new Thickness(0,0,0,4),
            Background = new SolidColorBrush(Color.FromArgb(65,245,248,252)),
            ToolTip = "只读参考代码，可选中复制；你的编辑器草稿保持不变。"
        });
        var copy = new Button { Content = "复制这段代码", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10,6,10,6) };
        copy.Click += (_,_) =>
        {
            try { Clipboard.SetText(code); notify("参考代码已复制；编辑器中的草稿保持不变。"); }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException) { notify("剪贴板暂不可用，可以选中参考代码手动复制。"); }
        };
        parent.Children.Add(copy);
    }
}
