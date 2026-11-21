using Avalonia.Controls;

namespace SententiamConverte.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // beim nächsten Öffnen des Projekts geht es im Editor dort weiter, wo man aufgehört hat
        Closing += (_, _) => (DataContext as ViewModels.MainViewModel)?.RememberPlace();
    }
}