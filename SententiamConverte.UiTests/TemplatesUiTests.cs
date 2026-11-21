using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Projects;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Projektvorlagen und QA-Profile je Kunde.</summary>
public class TemplatesUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Create(MainViewModel vm, string name, string targets, ProjectTemplate? template = null)
    {
        await Run(vm.BeginNewProjectCommand);
        vm.NewTemplate = template;
        vm.NewName = name + " " + Guid.NewGuid().ToString("N")[..4];
        if (template == null) vm.NewTargets = targets;
        await Run(vm.CreateProjectCommand);
    }

    [AvaloniaFact]
    public async Task AClientTemplateSetsUpTheNextProject()
    {
        using var home = TestSetup.OwnDataFolder();                        // legt gemeinsame Datenbanken an
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        AppServices.Platform = new FakePlatform();
        var client = "Kunde " + Guid.NewGuid().ToString("N")[..4];

        await Create(vm, "Vorlagenquelle", "en-GB, pl-PL");
        var first = vm.Project!;
        vm.ProjectTopic = "Mietrecht";
        vm.Qa!.MaxChars = 80;
        vm.ForbiddenWords = "basically";
        vm.Privacy.Enabled = true;
        vm.RatePrice = 0.15m;
        vm.TemplateName = client;
        await Run(vm.SaveAsTemplateCommand);
        Assert.Contains(vm.Templates, t => t.Name == client);
        vm.QaProfileName = client + " streng";
        await Run(vm.SaveQaProfileCommand);
        Assert.Equal(client + " streng", vm.SelectedQaProfile?.Name);

        // neues Projekt mit der Vorlage: Sprachen, Fachgebiet, QA, Datenschutz, Preise kommen mit
        var template = vm.Templates.First(t => t.Name == client);
        await Run(vm.BeginNewProjectCommand);
        vm.NewTemplate = template;
        Assert.Equal("en-GB, pl-PL", vm.NewTargets);
        Assert.Equal("Mietrecht", vm.NewTopic);
        vm.NewName = "Aus Vorlage " + Guid.NewGuid().ToString("N")[..4];
        await Run(vm.CreateProjectCommand);
        var second = vm.Project!;
        Assert.NotSame(first, second);
        Assert.Equal(["en-GB", "pl-PL"], second.TargetLanguages);
        Assert.Equal(80, second.Qa.MaxChars);
        Assert.Contains("basically", second.Qa.ForbiddenWords);
        Assert.True(second.Privacy.Enabled);
        Assert.Equal(0.15m, second.Rates.Price);
        Assert.Equal(80, vm.Qa!.MaxChars);                                  // auch in der Anzeige

        // QA-Profil in ein Projekt ohne Vorlage übernehmen
        vm.NewTemplate = null;
        await Create(vm, "Ohne Vorlage", "fr-FR");
        Assert.Equal(0, vm.Project!.Qa.MaxChars);
        vm.SelectedQaProfile = vm.QaProfileList.First(p => p.Name == client + " streng");
        await Run(vm.LoadQaProfileCommand);
        Assert.Equal(80, vm.Project.Qa.MaxChars);
        Assert.Equal("basically", vm.ForbiddenWords);

        await Run(vm.DeleteQaProfileCommand);
        Assert.DoesNotContain(vm.QaProfileList, p => p.Name == client + " streng");
        vm.NewTemplate = vm.Templates.First(t => t.Name == client);
        await Run(vm.DeleteTemplateCommand);
        Assert.DoesNotContain(ProjectTemplates.Load(), t => t.Name == client);
    }
}
