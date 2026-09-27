using System.Linq;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.ContinueWatching.Controllers;

public sealed class AddActionFilterConvention(string controllerFullName, string actionName, IFilterMetadata filter) : IApplicationModelConvention
{
    private readonly string _controllerFullName = controllerFullName;
    private readonly string _actionName = actionName;
    private readonly IFilterMetadata _filter = filter;

    public void Apply(ApplicationModel application)
    {
        var controller = application.Controllers.SingleOrDefault(
            controller => controller.ControllerType.FullName == _controllerFullName);

        if (controller is null)
        {
            return;
        }

        foreach (ActionModel action in controller.Actions.Where(action => action.ActionMethod.Name == _actionName))
        {
            action.Filters.Add(_filter);
        }
    }
}
