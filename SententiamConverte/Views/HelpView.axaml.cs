using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using SententiamConverte.ViewModels;

namespace SententiamConverte.Views;

public partial class HelpView : UserControl
{
    public HelpView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is HelpViewModel vm)
            vm.PropertyChanged += (_, a) =>
            {
                // Neues Kapitel beginnt oben
                if (a.PropertyName == nameof(HelpViewModel.Selected)) Content.Offset = new Vector(0, 0);
            };
    }
}
