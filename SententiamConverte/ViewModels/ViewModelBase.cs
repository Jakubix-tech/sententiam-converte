using System.Collections.Concurrent;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SententiamConverte.Core.Diagnostics;

namespace SententiamConverte.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    private static readonly ConcurrentDictionary<(System.Type, string), string> LastLogged = new();

    /// <summary>
    /// Jede Meldung, die die Oberfläche zeigt (Eigenschaften „…Status“, „Result“, „Message“), kommt ins Ablaufprotokoll –
    /// so lässt sich jeder Schritt nachvollziehen. Dieselbe Meldung zweimal hintereinander wird nur einmal festgehalten.
    /// </summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!ActivityLog.Enabled || e.PropertyName is not { } name || !(name.EndsWith("Status") || name is "Result" or "Message")) return;
        if (GetType().GetProperty(name)?.GetValue(this) is not string { Length: > 0 } text) return;
        var key = (GetType(), name);
        if (LastLogged.TryGetValue(key, out var last) && last == text) return;
        LastLogged[key] = text;
        ActivityLog.Write("Oberfläche", GetType().Name.Replace("ViewModel", "") + "." + name, null, text);
    }
}
