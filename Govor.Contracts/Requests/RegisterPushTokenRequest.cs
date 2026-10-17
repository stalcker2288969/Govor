using System.ComponentModel.DataAnnotations;

namespace Govor.Contracts.Requests;

public class RegisterPushTokenRequest
{
    [Required, StringLength(512)]
    public string Token { get; set; } = string.Empty;

    [Required, StringLength(50)]
    public string Platform { get; set; } = string.Empty;
}
