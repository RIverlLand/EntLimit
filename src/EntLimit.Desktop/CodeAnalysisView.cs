using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EntLimit.Core;

namespace EntLimit.Desktop;

internal sealed class CodeAnalysisView : StackPanel
{
    private readonly TextBlock _version;
    public CodeAnalysisView(CodeAnalysis analysis, string statusMessage)
    {
        _version = Paragraph(""); _version.ToolTip = "代码快照 SHA256 · " + analysis.CodeHash; Children.Add(_version);
        Children.Add(Heading(analysis.AstAvailable ? "这份代码的结构分析" : "尚未完成 AST 识别 · 通用学习参考"));
        if (!analysis.AstAvailable && !string.IsNullOrWhiteSpace(statusMessage)) Children.Add(Paragraph(statusMessage));
        Children.Add(Paragraph(analysis.Summary));
        Children.Add(Paragraph("本地结构分析仅覆盖部分语法模式。请结合运行结果判断建议是否适用。"));
        if (analysis.AstAvailable)
        {
            foreach (var finding in analysis.Findings)
            {
                Children.Add(Heading((finding.Line > 0 ? $"第 {finding.Line} 行 · " : "") + finding.Title));
                Children.Add(Paragraph(finding.Message));
                if (!string.IsNullOrWhiteSpace(finding.Evidence)) Children.Add(Code(finding.Evidence));
            }
        }
        else Children.Add(Paragraph("当前无法根据代码结构判断写法；下面的教程仅供对照，不表示你的代码包含这些问题。"));
        foreach (var tutorial in analysis.Tutorials)
        {
            var details = new StackPanel();
            details.Children.Add(Paragraph(tutorial.Explanation));
            for (int i=0;i<tutorial.Steps.Count;i++) details.Children.Add(Paragraph($"{i+1}. {tutorial.Steps[i]}"));
            if (!string.IsNullOrWhiteSpace(tutorial.Code)) { details.Children.Add(Heading("参考写法 · 不会替换编辑器")); details.Children.Add(Code(tutorial.Code)); }
            if (!string.IsNullOrWhiteSpace(tutorial.Practice)) { details.Children.Add(Heading("试着改一小步")); details.Children.Add(Paragraph(tutorial.Practice)); }
            if (Uri.TryCreate(tutorial.SourceUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https")
            {
                var source = new Button { Content = "查看官方参考 ↗", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10,6,10,6) };
                source.Click += (_,_) => { try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { source.Content = "未能打开浏览器，可稍后重试"; } };
                details.Children.Add(source);
            }
            Children.Add(new Expander { Header = tutorial.Title, IsExpanded = false, Content = details, Margin = new Thickness(0,14,0,4), HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        if (!string.IsNullOrWhiteSpace(analysis.Limitations)) { Children.Add(Heading("分析范围")); Children.Add(Paragraph(analysis.Limitations)); }
    }
    public void SetStale(bool stale)
    {
        _version.Text = stale ? "代码已修改：以下内容对应上次分析的代码，请点击“分析当前解答”更新。" : "对应当前编辑器代码 · 分析不会授予许可，也不会修改你的草稿。";
        _version.Foreground = stale ? Brushes.DarkGoldenrod : Brushes.SeaGreen;
    }
    private static TextBlock Heading(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,12,0,6) };
    private static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 21, Margin = new Thickness(0,0,0,8) };
    private static TextBox Code(string text) => new() { Text = text, IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 230, Background = new SolidColorBrush(Color.FromArgb(65,245,248,252)), Margin = new Thickness(0,0,0,8) };
}
