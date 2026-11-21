using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SententiamConverte.Core.Privacy;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

[CollectionDefinition("Outbound", DisableParallelization = true)]
public sealed class OutboundCollection;

/// <summary>Datenausgang: Rückfrage vor dem Senden mit dem genauen Inhalt, Protokoll auf der Seite „KI einrichten“.</summary>
[Collection("Outbound")]
public class OutboundUiTests
{
    private static async Task Until(Func<bool> condition, int ms = 3000)
    {
        for (var i = 0; i < ms / 20 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task AsksBeforeSendingAndShowsTheLog()
    {
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        var outbound = vm.Outbound;
        outbound.ModeIndex = (int)OutboundMode.AskEveryTime;
        try
        {
            outbound.ClearLogCommand.Execute(null);                                            // auch Einträge früherer Tests
            // Senden: die Rückfrage zeigt Dienst und genauen Inhalt
            var first = Task.Run(() => Outbound.CheckAsync("api.example.com", OutboundKind.Ai, "[user]\nDer Vertrag mit [NAME_1] endet.", false));
            await Until(() => outbound.IsAsking);
            Assert.True(outbound.IsAsking);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("api.example.com", outbound.PendingTitle);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible && t.Text?.Contains("Der Vertrag mit [NAME_1] endet.") == true);
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "26-datenausgang-rueckfrage.png"));
            outbound.SendCommand.Execute(null);
            await first;

            // Nicht senden: die Anfrage scheitert, nichts geht hinaus
            var second = Task.Run(() => Outbound.CheckAsync("api.example.com", OutboundKind.Ai, "Zweiter Text.", false));
            await Until(() => outbound.IsAsking);
            outbound.DeclineCommand.Execute(null);
            await Assert.ThrowsAsync<OutboundDeclinedException>(() => second);
            await Until(() => outbound.Rows.Count >= 2);
            Assert.False(outbound.IsAsking);
            Assert.Equal([OutboundDecision.Declined, OutboundDecision.Sent], outbound.Rows.Select(r => r.Entry.Decision).Take(2));

            // Protokoll auf der Seite „KI einrichten“: Eintrag wählen zeigt den Inhalt
            vm.SelectedTab = MainViewModel.AiTab;
            outbound.Selected = outbound.Visible.First(r => r.Entry.Content.Contains("Der Vertrag"));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), t => t.Text?.Contains("Der Vertrag mit [NAME_1] endet.") == true);
            Assert.Contains("1", outbound.Summary);
            var card = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == Services.L.T("Datenausgang – was den Rechner verlässt"));
            card.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "27-datenausgang-protokoll.png"));
        }
        finally
        {
            outbound.ModeIndex = (int)OutboundMode.LogOnly;
        }
    }
}
