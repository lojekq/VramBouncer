using System.Diagnostics;
using System.IO;
using VramBouncer.Shell;

namespace VramBouncer.Features;

public partial class JournalPage
{
    public JournalPage()
    {
        InitializeComponent();
        DataContext = Vm.I;
        OpenLogs.Click += (_, _) =>
        {
            if (Args.Current.Demo) return;
            Directory.CreateDirectory(Log.Dir);
            Process.Start("explorer.exe", $"\"{Log.Dir}\"");
        };
        Loaded += (_, _) => { if (!Args.Current.Demo) Vm.I.LoadHistory(Store.History()); };
    }
}
