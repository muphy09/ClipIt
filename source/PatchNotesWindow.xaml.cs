using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace ClipIt;

public partial class PatchNotesWindow : Window
{
    public PatchNotesWindow()
    {
        InitializeComponent();
        string notes;
        try { notes = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "patchnotes")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Could not read patch notes: " + ex.Message);
            notes = "Patch notes are unavailable. Please try again after updating ClipIt.";
        }
        NotesViewer.Document = FormatNotes(notes);
    }

    // The patchnotes file supports Markdown headings, bullet lists and plain paragraphs.
    // Build document elements directly so file contents are always treated as text.
    internal static FlowDocument FormatNotes(string notes)
    {
        var document = new FlowDocument
        {
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCE, 0xDA, 0xEB)),
            PagePadding = new Thickness(26, 18, 26, 24)
        };
        var lists = new Stack<(int Indent, System.Windows.Documents.List Bullets)>();
        Paragraph? paragraph = null;
        foreach (string raw in notes.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) { paragraph = null; continue; }
            int level = line.TakeWhile(c => c == '#').Count();
            if (level is >= 1 and <= 6 && line.Length > level && line[level] == ' ')
            {
                lists.Clear();
                paragraph = null;
                var heading = new Paragraph(new Run(line[(level + 1)..].Trim()))
                {
                    FontSize = level <= 2 ? 24 : level == 3 ? 16 : 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(level <= 2 ? Color.FromRgb(0xA8, 0xCB, 0xFF) : Color.FromRgb(0xEF, 0xF4, 0xFC)),
                    Margin = new Thickness(0, document.Blocks.Count == 0 ? 0 : 22, 0, 12),
                    KeepWithNext = true
                };
                document.Blocks.Add(heading);
            }
            else if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("+ "))
            {
                int indent = 0;
                foreach (char c in raw.AsSpan(0, raw.Length - raw.TrimStart().Length))
                    indent += c == '\t' ? 4 - indent % 4 : 1;
                while (lists.TryPeek(out var parent) && parent.Indent > indent) lists.Pop();
                if (lists.Count == 0 || lists.Peek().Indent < indent)
                {
                    var bullets = new System.Windows.Documents.List
                    {
                        MarkerStyle = lists.Count == 0 ? TextMarkerStyle.Disc : TextMarkerStyle.Circle,
                        Padding = new Thickness(20, 0, 0, 0),
                        Margin = new Thickness(0, 0, 0, lists.Count == 0 ? 10 : 0)
                    };
                    if (lists.TryPeek(out var containingList)) containingList.Bullets.ListItems.LastListItem.Blocks.Add(bullets);
                    else document.Blocks.Add(bullets);
                    lists.Push((indent, bullets));
                }
                paragraph = new Paragraph(new Run(line[2..].Trim())) { Margin = new Thickness(0, 0, 0, 8), LineHeight = 21 };
                lists.Peek().Bullets.ListItems.Add(new ListItem(paragraph));
            }
            else if (paragraph != null)
            {
                paragraph.Inlines.Add(new Run(" " + line));
            }
            else
            {
                lists.Clear();
                paragraph = new Paragraph(new Run(line)) { Margin = new Thickness(0, 0, 0, 10), LineHeight = 21 };
                document.Blocks.Add(paragraph);
            }
        }
        if (document.Blocks.Count == 0) document.Blocks.Add(new Paragraph(new Run("No patch notes yet. Check back after the next update.")));
        return document;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
}
