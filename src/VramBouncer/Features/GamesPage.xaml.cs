using System.IO;
using System.Windows;
using Microsoft.Win32;
using VramBouncer.Shell;

namespace VramBouncer.Features;

public partial class GamesPage
{
    public GamesPage()
    {
        InitializeComponent();
        DataContext = Vm.I;
        Search.Text = Vm.I.Filter;
        Search.TextChanged += (_, _) => Vm.I.Filter = Search.Text;
        Refresh.Click += (_, _) => Engine.I?.RefreshCatalog();
        AddExe.Click += (_, _) =>
        {
            var d = new OpenFileDialog { Filter = Loc.T("Games.ExeFilter") + "|*.exe" };
            if (d.ShowDialog() == true) Add(Path.GetFileNameWithoutExtension(d.FileName), d.FileName);
        };
        AddFolder.Click += (_, _) =>
        {
            var d = new OpenFolderDialog();
            if (d.ShowDialog() == true) Add(Path.GetFileName(d.FolderName.TrimEnd('\\')), d.FolderName);
        };
    }

    static void Add(string name, string path)
    {
        var g = Settings.Current.Games;
        if (!g.Manual.Any(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)))
            g.Manual.Add(new() { Name = name, Path = path });
        g.Excluded.RemoveAll(r => string.Equals(r, path, StringComparison.OrdinalIgnoreCase));
        Settings.Save();
        Engine.I?.RefreshCatalog();
    }

    void NotAGame(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GameRow row) return;
        var g = Settings.Current.Games;
        if (row.Game.Source == "Manual") g.Manual.RemoveAll(m => string.Equals(m.Path, row.Root, StringComparison.OrdinalIgnoreCase));
        else g.Excluded.Add(row.Root);
        g.Included.RemoveAll(r => string.Equals(r, row.Root, StringComparison.OrdinalIgnoreCase));
        Settings.Save();
        Engine.I?.RefreshCatalog();
    }

    void HintAdd(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GameHint h) return;
        Add(h.Name, h.Exe);
        Engine.I?.Forget(h.Exe);
    }

    void HintNotGame(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GameHint h) return;
        if (!Settings.Current.Games.NotGames.Contains(h.Exe, StringComparer.OrdinalIgnoreCase)) Settings.Current.Games.NotGames.Add(h.Exe);
        Settings.Save();
        Engine.I?.Forget(h.Exe);
    }

    void IsAGame(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GameRow row) return;
        var g = Settings.Current.Games;
        if (g.Excluded.RemoveAll(r => string.Equals(r, row.Root, StringComparison.OrdinalIgnoreCase)) == 0) g.Included.Add(row.Root);
        Settings.Save();
        Engine.I?.RefreshCatalog();
    }
}
