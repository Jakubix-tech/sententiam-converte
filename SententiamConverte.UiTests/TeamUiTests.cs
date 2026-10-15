using System.Net;
using System.Net.Sockets;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Server;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Zwei Übersetzer an einem Teamserver – über die Oberfläche bedient.</summary>
public class TeamUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task SharedTmAndKnowledgeThroughTheUi()
    {
        var data = Path.Combine(TestSetup.Home, "server-" + Guid.NewGuid().ToString("N")[..6]);
        var users = new UserStore(data);
        var admin = users.Add("admin", Role.Admin);
        var anna = users.Add("Anna", Role.Editor);
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)l.LocalEndpoint).Port}";
        l.Stop();
        await using var server = ServerApp.Build(["--urls", url], data);
        await server.StartAsync();

        var platform = new FakePlatform();
        platform.FilesToPick.Add(("anleitung.docx", UiFlowTests.SampleDocx()));
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        AppServices.Platform = platform;

        // Teamserver in den Einstellungen eintragen (als Admin) und Ressourcen anlegen
        var settings = vm.Settings;
        await Run(settings.AddServerCommand);
        settings.SelectedServer!.Name = "Büro";
        settings.SelectedServer.Url = url;
        settings.SelectedServer.Token = admin;
        await Run(settings.TestServerCommand);
        Assert.Contains("Angemeldet als admin", settings.ServerResult);
        settings.NewResourceKind = (int)ResourceKind.Tm;
        settings.NewResourceName = "Team-TM";
        await Run(settings.CreateServerResourceCommand);
        settings.NewResourceKind = (int)ResourceKind.Knowledge;
        settings.NewResourceName = "Team-Wissen";
        await Run(settings.CreateServerResourceCommand);

        // Ab jetzt arbeitet Anna
        settings.SelectedServer.Token = anna;
        await Run(settings.SaveCommand);
        vm.SelectedTab = 5;
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "12-teamserver.png"));

        // Projekt mit gemeinsamen Ressourcen (TM nur für en-GB, zurückschreibend)
        await Run(vm.BeginNewProjectCommand);
        vm.NewName = "Team " + Guid.NewGuid().ToString("N")[..4];
        vm.NewTargets = "en-GB, pl-PL";
        await Run(vm.CreateProjectCommand);
        await Run(vm.AddFilesCommand);

        var res = vm.Resources;
        res.AttachServer = "Büro";
        res.AttachKind = (int)ResourceKind.Tm;
        for (var i = 0; i < 100 && res.RemoteNames.Count == 0; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        res.AttachRemoteName = "Team-TM";
        res.AttachLanguages = "en-GB";
        res.AttachWriteBack = true;
        await Run(res.AttachRemoteCommand);
        res.AttachKind = (int)ResourceKind.Knowledge;
        for (var i = 0; i < 100 && !res.RemoteNames.Contains("Team-Wissen"); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        res.AttachRemoteName = "Team-Wissen";
        res.AttachLanguages = "";
        await Run(res.AttachRemoteCommand);
        Assert.Equal(2, res.Attached.Count);
        Assert.Equal("", res.ResourceErrors);
        vm.SelectedTab = 2;
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "13-ressourcen-team.png"));

        // Wissen über die Oberfläche anlegen
        var kn = vm.Knowledge;
        kn.SelectedBase = kn.Bases.First(b => b.Name == "Team-Wissen");
        await Run(kn.NewEntryCommand);
        kn.EditLanguage = "en-GB";
        kn.EditKind = (int)KnowledgeKind.Style;
        kn.EditTitle = "Instructions use imperative";
        kn.EditContent = "Operating instructions address the reader with the imperative (e.g. Switch off the device).";
        await Run(kn.SaveEntryCommand);
        Assert.Single(kn.Entries);
        await Run(kn.LoadActivityCommand);
        Assert.Contains(kn.Activity, a => a.Entry.User == "Anna");
        vm.SelectedTab = 3;
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "14-wissen-team.png"));

        // Übersetzen und bestätigen → landet im Team-TM
        await Run(vm.OpenInEditorCommand, vm.Files.First(f => f.Language == "en-GB"));
        var editor = vm.Editor;
        editor.Selected = editor.Segments[0];
        editor.Selected.Target = "Operating instructions";
        await Run(editor.ConfirmAndNextCommand);

        var teamTm = new RemoteTranslationMemory(ResourceFactory.Api(AppServices.Servers.Servers[0]), "Team-TM");
        var hit = teamTm.Search("de-DE", "en-GB", "Betriebsanleitung").Single();
        Assert.Equal("Operating instructions", hit.Unit.Target);
        Assert.Equal("Anna", hit.Unit.Origin);

        // Wissen erscheint beim passenden Segment im Editor
        editor.Selected = editor.Segments.First(s => s.Source.Contains("einschalten"));
        for (var i = 0; i < 100 && editor.Knowledge.Count == 0; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        Assert.Contains(editor.Knowledge, k => k.Title == "Instructions use imperative");
        editor.SidePanelTab = 4;
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "15-editor-wissen.png"));
    }
}
