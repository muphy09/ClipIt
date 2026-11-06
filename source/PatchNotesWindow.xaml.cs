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
        System.Windows.Documents.List? bullets = null;
        Paragraph? paragraph = null;
        foreach (string raw in notes.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) { paragraph = null; continue; }
            int level = line.TakeWhile(c => c == '#').Count();
            if (level is >= 1 and <= 6 && line.Length > level && line[level] == ' ')
            {
                bullets = null;
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
                if (bullets == null)
                {
                    bullets = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Disc, Padding = new Thickness(20, 0, 0, 0), Margin = new Thickness(0, 0, 0, 10) };
                    document.Blocks.Add(bullets);
                }
                paragraph = new Paragraph(new Run(line[2..].Trim())) { Margin = new Thickness(0, 0, 0, 8), LineHeight = 21 };
                bullets.ListItems.Add(new ListItem(paragraph));
            }
            else if (paragraph != null)
            {
                paragraph.Inlines.Add(new Run(" " + line));
            }
            else
            {
                bullets = null;
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
