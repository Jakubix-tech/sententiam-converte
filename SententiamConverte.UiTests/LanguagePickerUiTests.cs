using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Controls;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Sprachauswahl statt Codes eintippen.</summary>
public class LanguagePickerUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task LanguagesAreChosenByNameAndShownAsTags()
    {
        Assert.Contains(LanguageOption.All, c => c.Code == "pl-PL" && c.Name == "Polnisch");
        Assert.Contains(LanguageOption.All, c => c.Code == "eu-ES");                       // auch seltenere Sprachen
        Assert.True(LanguageOption.For("pl-PL").Matches("poln"));
        Assert.True(LanguageOption.For("pl-PL").Matches("polski"));                         // Eigenname
        Assert.True(LanguageOption.For("pl-PL").Matches("pl"));

        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        AppServices.Platform = new FakePlatform();
        await Run(vm.BeginNewProjectCommand);
        vm.NewTargets = "en-GB, pl-PL";
        Dispatcher.UIThread.RunJobs();

        var pickers = window.GetVisualDescendants().OfType<LanguagePicker>().Where(p => p.IsEffectivelyVisible && p.DataContext is MainViewModel).ToList();   // nicht die aus „Erste Schritte“
        var targets = pickers.First(p => p.Multiple);
        var chips = targets.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Polnisch · pl-PL", chips);
        Assert.Contains("Englisch (UK) · en-GB", chips);
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "49-sprachauswahl.png"));

        // ✕ an der ersten Marke entfernt Englisch
        targets.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "✕").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("pl-PL", vm.NewTargets);

        // Namen eintippen und mit Eingabe bestätigen: der Code wird übernommen; ein unbekannter Code geht auch
        var source = pickers.First(p => !p.Multiple);
        var box = source.GetVisualDescendants().OfType<AutoCompleteBox>().First();
        box.Text = "Französisch";
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal("fr-FR", vm.NewSource);
        box.Text = "eu";
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal("eu", vm.NewSource);
        box.Text = "Unsinn";
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal("eu", vm.NewSource);                                                 // Unbekanntes ändert nichts
        // Dreibuchstaben-Codes werden zur Sprache der Liste („fra“ ist Französisch, nicht ein eigener Code)
        foreach (var (typed, code) in new[] { ("fra", "fr-FR"), ("deu-AT", "de-AT"), ("pol", "pl-PL"), ("eng", "en-GB"), ("ces", "cs-CZ") })
        {
            box.Text = typed;
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal(code, vm.NewSource);
        }
        await Run(vm.CancelNewProjectCommand);
    }
}
