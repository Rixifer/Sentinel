using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Sentinel.Core;
using Sentinel.Data;
using Sentinel.UI;
using System.Collections.Generic;

namespace Sentinel;

public class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager        Commands         { get; private set; } = null!;
    [PluginService] internal static IObjectTable           ObjectTable      { get; private set; } = null!;
    [PluginService] internal static IDataManager           DataManager      { get; private set; } = null!;
    [PluginService] internal static IGameGui               GameGui          { get; private set; } = null!;
    [PluginService] internal static IPluginLog             Log              { get; private set; } = null!;
    [PluginService] internal static IFramework             Framework        { get; private set; } = null!;
    [PluginService] internal static IChatGui               ChatGui          { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider   GameInterop      { get; private set; } = null!;
    [PluginService] internal static ISigScanner            SigScanner       { get; private set; } = null!;
    [PluginService] internal static ICondition             Condition        { get; private set; } = null!;

    private const string CmdMain = "/sentinel";

    public Configuration            Config   { get; }
    internal readonly OmenSheetReader      _omenReader;
    internal readonly NetworkCastListener  _netListener;
    internal readonly CastScanner          _scanner;
    internal readonly OmenManager          _omenManager;
    internal readonly OmenVfxTracker       _vfxTracker = new();
    internal readonly BmrShapeLoader         _bmrShapes;
    internal readonly CustomOmenSpawner      _customSpawner;
    internal readonly ExcludedActionLoader   _excludedActions;

    // Kept for potential future use
    internal readonly AoEResolver            _resolver;

    private readonly WindowSystem _windowSystem = new("Sentinel");
    private readonly ConfigWindow _configWindow;
    private readonly DebugWindow  _debugWindow;
    private readonly WorldOverlay _worldOverlay;

    internal IReadOnlyList<ActiveCast> _lastCasts = new List<ActiveCast>();

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        if (Config.Version < 12)
        {
            Config.Version = 12;
            Config.Save();
            Log.Information("[Sentinel] Config updated to version 12.");
        }

        _omenReader      = new OmenSheetReader(DataManager);
        _resolver        = new AoEResolver(DataManager);
        VfxFunctions.Initialize(SigScanner);
        _omenManager     = new OmenManager(Config, GameInterop, _omenReader);
        _bmrShapes       = new BmrShapeLoader(PluginInterface.AssemblyLocation.DirectoryName!);
        _excludedActions = new ExcludedActionLoader(PluginInterface.AssemblyLocation.DirectoryName!);
        _customSpawner   = new CustomOmenSpawner(_vfxTracker, _omenManager, Config);
        _netListener     = new NetworkCastListener(GameInterop, SigScanner);
        _scanner         = new CastScanner(ObjectTable, DataManager, Config, _omenReader, _omenManager,
                               _customSpawner, _bmrShapes, _netListener, _excludedActions, _resolver);

        _configWindow = new ConfigWindow(this);
        _debugWindow  = new DebugWindow(this);
        _worldOverlay = new WorldOverlay(this);
        _windowSystem.AddWindow(_configWindow);
        _windowSystem.AddWindow(_debugWindow);

        Commands.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "/sentinel — open settings | on/off — enable/disable | debug — debug overlay",
        });

        Framework.Update                       += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw         += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;

        Log.Information("[Sentinel] Loaded. {OmenCount} omen paths, {BmrCount} BMR shapes, {ExcCount} excluded actions.",
                        _omenReader.OmenPaths.Count, _bmrShapes.Shapes.Count, _excludedActions.Count);
    }

    public void Dispose()
    {
        Framework.Update                       -= OnFrameworkUpdate;
        _netListener.Dispose();
        _omenManager.Dispose();
        _vfxTracker.Dispose();
        Commands.RemoveHandler(CmdMain);
        PluginInterface.UiBuilder.Draw         -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        _windowSystem.RemoveAllWindows();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        _lastCasts = _scanner.Scan(); // scan + recolor + custom omen spawning inside Scan()
        _vfxTracker.Update();         // destroy VFX for casts that ended this frame
    }

    private void DrawUI()
    {
        _windowSystem.Draw();
        _worldOverlay.Draw();
        _debugWindow.Update(_lastCasts);
    }

    private void OpenConfig() => _configWindow.IsOpen = true;

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on":
                Config.Enabled = true;
                Config.Save();
                ChatGui.Print("[Sentinel] Enabled");
                break;

            case "off":
                Config.Enabled = false;
                Config.Save();
                ChatGui.Print("[Sentinel] Disabled");
                break;

            case "debug":
                _debugWindow.IsOpen = !_debugWindow.IsOpen;
                break;

            default:
                _configWindow.IsOpen = true;
                break;
        }
    }
}
