using Microsoft.AspNetCore.Mvc;
using MusicAI.Common.Models;

[ApiController]
[Route("api/[controller]")]
public class SubscriptionController : ControllerBase
{
    private readonly Func<string, User> _getUser;
    public SubscriptionController(Func<string, User> getUser) { _getUser = getUser; }

    [HttpGet("status/{userId}")]
    public IActionResult Status(string userId)
    {
        var u = _getUser(userId);
        return Ok(new { userId = u.Id, isSubscribed = u.IsSubscribed, credits = u.Credits });
    }
}
