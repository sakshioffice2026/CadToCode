using CadToCode.Business.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Web.ViewComponents;

public class RecentProjectsViewComponent : ViewComponent
{
    private const int DefaultTake = 8;

    private readonly IProjectProcessingService _processingService;

    public RecentProjectsViewComponent(IProjectProcessingService processingService)
    {
        _processingService = processingService;
    }

    public async Task<IViewComponentResult> InvokeAsync(int take = DefaultTake)
    {
        var items = await _processingService.GetHistoryAsync(take, HttpContext.RequestAborted);

        var currentId = ViewContext.RouteData.Values["id"]?.ToString();
        ViewData["CurrentProjectId"] = currentId;

        return View(items);
    }
}