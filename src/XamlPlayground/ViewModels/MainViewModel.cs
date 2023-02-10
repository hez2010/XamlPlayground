﻿using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Octokit;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.Reactive.Linq;
using ReactiveMarbles.PropertyChanged;
using XamlPlayground.Services;
using System.Reactive.Subjects;
using Avalonia.Threading;
using Avalonia.ReactiveUI;

namespace XamlPlayground.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty] private ObservableCollection<SampleViewModel> _samples;
    [ObservableProperty] private SampleViewModel? _currentSample;
    [ObservableProperty] private Control? _control;
    [ObservableProperty] private bool _enableAutoRun;
    [ObservableProperty] private string? _lastErrorMessage;
    [ObservableProperty] private int _editorFontSize;
    private bool _update;
    private (Assembly? Assembly, AssemblyLoadContext? Context)? _previous;
    private IStorageFile? _openXamlFile;
    private IStorageFile? _openCodeFile;
    private readonly Subject<(string? xaml, string? code)> _runSubject;

    public MainViewModel(string? initialGist)
    {
        _runSubject = new Subject<(string? xaml, string? code)>();
        _editorFontSize = 12;
        _samples = GetSamples(".xml");
        _enableAutoRun = true;

        OpenXamlFileCommand = new AsyncRelayCommand(async () => await OpenXamlFile());
        SaveXamlFileCommand = new AsyncRelayCommand(async () => await SaveXamlFile());
        OpenCodeFileCommand = new AsyncRelayCommand(async () => await OpenCodeFile());
        SaveCodeFileCommand = new AsyncRelayCommand(async () => await SaveCodeFile());
        RunCommand = new RelayCommand(() => Run(_currentSample?.Xaml.Text, _currentSample?.Code.Text));
        GistCommand = new AsyncRelayCommand<string?>(Gist);

        this.WhenChanged(x => x.CurrentSample)
            .DistinctUntilChanged()
            .Subscribe(CurrentSampleChanged);

        _runSubject.AsObservable()
            .Throttle(TimeSpan.FromMilliseconds(400))
            .ObserveOn(AvaloniaScheduler.Instance)
            .Subscribe(OnRun);

        async void OnRun((string? xaml, string? code) x)
        {
            await RunInternal(x.xaml, x.code);
        }

        if (!string.IsNullOrEmpty(initialGist))
        {
            Gist(initialGist);
        }
        else
        {
            CurrentSample = _samples.FirstOrDefault(x => x.Name == "Demo");
        }
    }

    public ICommand RunCommand { get; }

    public ICommand GistCommand { get; }

    public ICommand OpenXamlFileCommand { get; }

    public ICommand SaveXamlFileCommand { get; }

    public ICommand OpenCodeFileCommand { get; }

    public ICommand SaveCodeFileCommand { get; }

    private void CurrentSampleChanged(SampleViewModel? sampleViewModel)
    {
        if (sampleViewModel is { })
        {
            Open(sampleViewModel);
        }
    }

    private async Task<(string Xaml, string Code)> GetGistContent(string id)
    {
        var client = new GitHubClient(new ProductHeaderValue("XamlPlayground"));
        var gist = await client.Gist.Get(id);
        var xaml = gist.Files
            .FirstOrDefault(x => string.Compare(x.Key, "Main.axaml", StringComparison.OrdinalIgnoreCase) == 0)
            .Value;
        var code = gist.Files
            .FirstOrDefault(x => string.Compare(x.Key, "Main.axaml.cs", StringComparison.OrdinalIgnoreCase) == 0)
            .Value;
        return (xaml?.Content ?? "", code?.Content ?? "");
    }

    public async Task Gist(string? id)
    {
        if (id is null)
        {
            return;
        }
        try
        {
            var (xaml, code) = await GetGistContent(id);
            var sample = new SampleViewModel("Gist", xaml, code, Open, AutoRun);
            _samples.Insert(0, sample);
            CurrentSample = sample; 
            AutoRun(CurrentSample);
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
        }
    }

    private string? GetSampleName(string resourceName)
    {
        var parts = resourceName.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[^2]}" : null;
    }

    private string? LoadResourceString(string name)
    {
        var assembly = typeof(MainViewModel).Assembly;
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream == null)
        {
            return null;
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private ObservableCollection<SampleViewModel> GetSamples(string sampleExtension)
    {
        var samples = new ObservableCollection<SampleViewModel>();
        var assembly = typeof(MainViewModel).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();

        samples.Add(new SampleViewModel("Code", Templates.s_xaml, Templates.s_code, Open, AutoRun));

        foreach (var resourceName in resourceNames)
        {
            if (!resourceName.EndsWith(sampleExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (LoadResourceString(resourceName) is { } xaml)
            {
                if (GetSampleName(resourceName) is { } name)
                {
                    samples.Add(new SampleViewModel(name, xaml, string.Empty, Open, AutoRun));
                }
            }
        }

        return samples;
    }

    private void Open(SampleViewModel sampleViewModel)
    {
        Control = null;
        LastErrorMessage = null;

        CurrentSample = sampleViewModel;

        if (_enableAutoRun)
        { 
            Run(sampleViewModel.Xaml.Text, sampleViewModel.Code.Text);
        }
    }

    private static List<FilePickerFileType> GetXamlFileTypes()
    {
        return new List<FilePickerFileType>
        {
            StorageService.Axaml,
            StorageService.Xaml,
            StorageService.All
        };
    }

    private static List<FilePickerFileType> GetCodeFileTypes()
    {
        return new List<FilePickerFileType>
        {
            StorageService.CSharp,
            StorageService.All
        };
    }

    private void AutoRun(SampleViewModel sampleViewModel)
    {
        if (EnableAutoRun)
        { 
            Run(sampleViewModel.Xaml.Text, sampleViewModel.Code.Text);
        }
    }

    private void Run(string? xaml, string? code)
    {
        _runSubject.OnNext((xaml, code));
    }

    private async Task RunInternal(string? xaml, string? code)
    {
        if (_update)
            return;

        _update = true;
        try
        {
            // Control = null;
#if false
            if (!Utilities.IsBrowser())
            {
                // TODO: Unload previously loaded assembly.
                if (_previous is { })
                {
                    _previous?.Context?.Unload();
                    _previous = null;

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
 #endif
            Assembly? scriptAssembly = null;

            if (code is { } && !string.IsNullOrWhiteSpace(code))
            {
                try
                {
                    _previous = await Task.Run(async () => await CompilerService.GetScriptAssembly(code));
                    if (_previous?.Assembly is { })
                    {
                        scriptAssembly = _previous?.Assembly;
                        Console.WriteLine($"Compiled assembly: {scriptAssembly?.GetName().Name}");
                    }
                    else
                    {
                        throw new Exception("Failed to compile code.");
                    }
                }
                catch (Exception exception)
                {
                    LastErrorMessage = exception.Message;
                    Console.WriteLine(exception);
                    return;
                }
            }

            if (scriptAssembly is { })
            {
                var types = scriptAssembly.GetTypes();
                var type = types.FirstOrDefault(x => x.Name == "SampleView");
                if (type != null)
                {
                    var rootInstance = Activator.CreateInstance(type);

                    using var stream = new MemoryStream();
                    var writer = new StreamWriter(stream);
                    writer.Write(xaml);
                    writer.Flush();
                    stream.Position = 0;

                    var control = AvaloniaRuntimeXamlLoader.Load(stream, scriptAssembly, rootInstance);
                    if (control is { })
                    {
                        Control = (Control)control;
                        LastErrorMessage = null;
                    }
                }
            }
            else
            {
                var control = AvaloniaRuntimeXamlLoader.Parse<Control?>(xaml, null);
                if (control is { })
                {
                    Control = control;
                    LastErrorMessage = null;
                }
            }
        }
        catch (Exception exception)
        {
            LastErrorMessage = exception.Message;
            Console.WriteLine(exception);
        }
        finally
        {
            _update = false;
        }
    }

    private async Task OpenXamlFile()
    {
        if (CurrentSample is null)
        {
            return;
        }

        var storageProvider = StorageService.GetStorageProvider();
        if (storageProvider is null)
        {
            return;
        }

        var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open xaml",
            FileTypeFilter = GetXamlFileTypes(),
            AllowMultiple = false
        });

        var file = result.FirstOrDefault();
        if (file is not null)
        {
            if (file.CanOpenRead)
            {
                try
                {
                    _openXamlFile = file;
                    await using var stream = await _openXamlFile.OpenReadAsync();
                    using var reader = new StreamReader(stream);
                    var fileContent = await reader.ReadToEndAsync();
                    CurrentSample.Xaml.Text = fileContent; 
                    AutoRun(CurrentSample);
                    reader.Dispose();
                }
                catch (Exception exception)
                {
                    Console.WriteLine(exception);
                }
            }
        }
    }

    private async Task SaveXamlFile()
    {
        if (CurrentSample is null)
        {
            return;
        }

        if (_openXamlFile is null)
        {
            var storageProvider = StorageService.GetStorageProvider();
            if (storageProvider is null)
            {
                return;
            }

            var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save xaml",
                FileTypeChoices = GetXamlFileTypes(),
                SuggestedFileName = Path.GetFileNameWithoutExtension("playground"),
                DefaultExtension = "axaml",
                ShowOverwritePrompt = true
            });

            if (file is not null)
            {
                if (file.CanOpenWrite)
                {
                    try
                    {
                        _openXamlFile = file;
                        await using var stream = await _openXamlFile.OpenWriteAsync();
                        await using var writer = new StreamWriter(stream);
                        await writer.WriteAsync(CurrentSample.Xaml.Text);
                    }
                    catch (Exception exception)
                    {
                        Console.WriteLine(exception);
                    }
                }
            }
        }
        else if (_openXamlFile.CanOpenWrite)
        {
            await using var stream = await _openXamlFile.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(CurrentSample.Xaml.Text);
        }
    }

    private async Task OpenCodeFile()
    {
        if (CurrentSample is null)
        {
            return;
        }

        var storageProvider = StorageService.GetStorageProvider();
        if (storageProvider is null)
        {
            return;
        }

        var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open code",
            FileTypeFilter = GetCodeFileTypes(),
            AllowMultiple = false
        });

        var file = result.FirstOrDefault();
        if (file is not null)
        {
            if (file.CanOpenRead)
            {
                try
                {
                    _openCodeFile = file;
                    await using var stream = await _openCodeFile.OpenReadAsync();
                    using var reader = new StreamReader(stream);
                    var fileContent = await reader.ReadToEndAsync();
                    CurrentSample.Code.Text = fileContent;
                    AutoRun(CurrentSample);
                    reader.Dispose();
                }
                catch (Exception exception)
                {
                    Console.WriteLine(exception);
                }
            }
        }
    }

    private async Task SaveCodeFile()
    {
        if (CurrentSample is null)
        {
            return;
        }

        if (_openCodeFile is null)
        {
            var storageProvider = StorageService.GetStorageProvider();
            if (storageProvider is null)
            {
                return;
            }

            var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save code",
                FileTypeChoices = GetCodeFileTypes(),
                SuggestedFileName = Path.GetFileNameWithoutExtension("playground"),
                DefaultExtension = "cs",
                ShowOverwritePrompt = true
            });

            if (file is not null)
            {
                if (file.CanOpenWrite)
                {
                    try
                    {
                        _openCodeFile = file;
                        await using var stream = await _openCodeFile.OpenWriteAsync();
                        await using var writer = new StreamWriter(stream);
                        await writer.WriteAsync(CurrentSample.Code.Text);
                    }
                    catch (Exception exception)
                    {
                        Console.WriteLine(exception);
                    }
                }
            }
        }
        else if (_openCodeFile.CanOpenWrite)
        {
            await using var stream = await _openCodeFile.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(CurrentSample.Code.Text);
        }
    }
}
