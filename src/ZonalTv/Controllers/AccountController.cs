using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using ZonalTv.Data;

namespace ZonalTv.Controllers;

public class AccountController : Controller
{
    private readonly UserManager<ZonalTvUser> _userManager;
    private readonly SignInManager<ZonalTvUser> _signInManager;
    private readonly ILogger<AccountController> _logger;

    public AccountController(UserManager<ZonalTvUser> userManager,
        SignInManager<ZonalTvUser> signInManager, ILogger<AccountController> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _logger = logger;
    }

    [HttpGet]
    [AllowAnonymous]
    [Route("/register")]
    public IActionResult Register()
    {
        return View();
    }

    [HttpPost]
    [AllowAnonymous]
    [Route("/register")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterModel model)
    {
        if (ModelState.IsValid)
        {
            var user = new ZonalTvUser{
                UserName = model.Username,
                Email = model.Email
            };
            var result = await _userManager.CreateAsync(user, model.Password);
            if (result.Succeeded)
            {
                await _signInManager.SignInAsync(user, isPersistent: true);
                _logger.LogInformation(
                    $"User '{model.Username}' created with e-mail address '{model.Email}'");
                return RedirectToAction("Index", "Home");
            }
        }

        // Redisplay form in case of errors
        return View(model);
    }
}