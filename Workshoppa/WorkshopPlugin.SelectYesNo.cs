using System;
namespace Workshoppa;

partial class WorkshopPlugin
{
    internal void ConfirmCollectProductFollowUp()
    {
        _configuration.CurrentlyCraftedItem = null;
        _pluginInterface.SavePluginConfig(_configuration);

        CurrentStage = Stage.TakeItemFromQueue;
        _continueAt = DateTime.Now.AddSeconds(0.5);
    }
}
