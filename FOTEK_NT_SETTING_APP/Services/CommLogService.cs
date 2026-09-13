using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace FOTEK_NT_SETTING_APP.Services;

public sealed class CommLogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Level { get; init; } = "INFO";
    public string Message { get; init; } = string.Empty;
    public string Display => $"{Time:HH:mm:ss.fff} [{Level}] {Message}";
}

/// <summary>UI-thread safe communication diagnostic log (keeps last N lines).</summary>
public sealed class CommLogService
{
    private const int MaxEntries = 500;
    private readonly Dispatcher _dispatcher;

    public ObservableCollection<CommLogEntry> Entries { get; } = [];

    public CommLogService(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? Application.Current.Dispatcher;
    }

    public void Info(string message) => Add("INFO", message);
    public void Warn(string message) => Add("WARN", message);
    public void Error(string message) => Add("ERROR", message);

    public void Clear()
    {
        if (_dispatcher.CheckAccess())
            Entries.Clear();
        else
            _dispatcher.Invoke(Entries.Clear);
    }

    public string ExportText()
        => string.Join(Environment.NewLine, Entries.Select(e => e.Display));

    private void Add(string level, string message)
    {
        void append()
        {
            Entries.Add(new CommLogEntry { Level = level, Message = message });
            while (Entries.Count > MaxEntries)
                Entries.RemoveAt(0);
        }

        if (_dispatcher.CheckAccess())
            append();
        else
            _dispatcher.Invoke(append);
    }
}
