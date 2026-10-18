using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace EntLimit.Desktop;

// A non-interactive color overlay leaves the native TextBox caret, selection,
// input methods and undo stack intact. It never rewrites the document.
internal sealed class PythonHighlightAdorner : Adorner
{
    private readonly TextBox _editor;
    private static readonly Regex Tokens = new(@"#[^\r\n]*|'[^'\r\n]*'|""[^""\r\n]*""|\b(?:def|return|for|in|if|else|elif|while|class|import|from|as|and|or|not|is|None|True|False|pass|break|continue|try|except|raise|with|yield|lambda)\b|\b\d+(?:\.\d+)?\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(30));
    public PythonHighlightAdorner(TextBox editor) : base(editor) { _editor = editor; IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (_editor.ActualWidth < 1 || _editor.Text.Length > 65536) return;
        drawing.PushClip(new RectangleGeometry(new Rect(0,0,Math.Max(0,_editor.ActualWidth - 18),Math.Max(0,_editor.ActualHeight - 3))));
        try
        {
            foreach (Match match in Tokens.Matches(_editor.Text))
            {
                Rect rect = _editor.GetRectFromCharacterIndex(match.Index, false);
                if (rect.IsEmpty || rect.Bottom < 0 || rect.Top > _editor.ActualHeight) continue;
                Color color = match.Value[0] == '#' ? Color.FromRgb(91,134,112) : match.Value[0] is '\'' or '"' ? Color.FromRgb(157,104,50) : char.IsDigit(match.Value[0]) ? Color.FromRgb(60,124,158) : Color.FromRgb(130,82,174);
                var text = new FormattedText(match.Value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(_editor.FontFamily,_editor.FontStyle,_editor.FontWeight,_editor.FontStretch),_editor.FontSize,new SolidColorBrush(color),VisualTreeHelper.GetDpi(this).PixelsPerDip);
                drawing.DrawText(text,new Point(rect.Left,rect.Top + Math.Max(0,(rect.Height - text.Height)/2)));
            }
        }
        catch (RegexMatchTimeoutException) { }
        finally { drawing.Pop(); }
    }
}
