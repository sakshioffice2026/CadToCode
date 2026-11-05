using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Web.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;

    public HomeController(ILogger<HomeController> logger)
    {
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index()
    {
        return RedirectToAction("Dashboard");
    }

    [HttpGet]
    public IActionResult Privacy()
    {
        return View();
    }

    [HttpGet]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        var requestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        _logger.LogError("Unhandled error. RequestId: {RequestId}", requestId);

        ViewData["RequestId"] = requestId;
        return View();
    }

    [HttpGet]
    public IActionResult Dashboard()
    {
        return View("CadDashboard");
    }
}