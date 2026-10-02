using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using LLib.GameUI;

namespace Workshoppa;

internal sealed unsafe class WorkshopTransitionCutsceneSkip
{
    private readonly ICondition _condition;
    private readonly IPluginLog _pluginLog;
    private bool _pending;
    private bool _requested;
    private DateTime _expiresAt;
    private DateTime _nextAttempt;

    public WorkshopTransitionCutsceneSkip(ICondition condition, IPluginLog pluginLog)
    {
        _condition = condition;
        _pluginLog = pluginLog;
    }

    public void Arm()
    {
        _pending = true;
        _requested = false;
        _expiresAt = DateTime.Now.AddSeconds(15);
        _nextAttempt = DateTime.MinValue;
    }

    public void Clear()
    {
        _pending = false;
        _requested = false;
        _expiresAt = DateTime.MinValue;
        _nextAttempt = DateTime.MinValue;
    }

    public void TrySkip()
    {
        if (!_pending)
            return;

        var now = DateTime.Now;
        if (now >= _expiresAt)
        {
            _pluginLog.Warning("Workshop transition cutscene skip request expired before the skip prompt appeared");
            Clear();
            return;
        }

        // WatchingCutscene (58) is not the flag used by all of the game's current cutscene paths.
        // The UI builder considers OccupiedInCutSceneEvent and WatchingCutscene78 the active
        // cutscene conditions, so accept those as well for the workshop transition scenes.
        bool isWatchingCutscene = _condition[ConditionFlag.WatchingCutscene] ||
                                  _condition[ConditionFlag.WatchingCutscene78] ||
                                  _condition[ConditionFlag.OccupiedInCutSceneEvent];
        if (!isWatchingCutscene || _requested || now < _nextAttempt)
            return;

        _nextAttempt = now.AddMilliseconds(250);

        AgentModule* agentModule = AgentModule.Instance();
        if (agentModule == null)
            return;

        AgentCutscene* cutsceneAgent = (AgentCutscene*)agentModule->GetAgentByInternalId(AgentId.Cutscene);
        if (cutsceneAgent == null || cutsceneAgent->SkipCallback == null || cutsceneAgent->SkipDialogAddonId != 0)
            return;

        if (cutsceneAgent->OpenSkipDialog(cutsceneAgent->SkipCallback))
        {
            _pluginLog.Information("Opening skip prompt for workshop transition cutscene");
            _requested = true;
        }
    }

    public bool IsSkipDialog(AtkUnitBase* addon)
    {
        if (!_pending || addon == null)
            return false;

        AgentModule* agentModule = AgentModule.Instance();
        if (agentModule == null)
            return false;

        AgentCutscene* cutsceneAgent = (AgentCutscene*)agentModule->GetAgentByInternalId(AgentId.Cutscene);
        if (cutsceneAgent == null || cutsceneAgent->SkipDialogAddonId == 0)
            return false;

        return LAddon.GetAddonById(cutsceneAgent->SkipDialogAddonId) == addon;
    }
}
