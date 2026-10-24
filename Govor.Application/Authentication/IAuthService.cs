using Govor.Domain.Common;
using Govor.Domain.Models;
using Govor.Domain.Models.Users;
using SmartRes;

namespace Govor.Application.Authentication;

public interface IAccountService
{
    public Task<Result<User, Error>> RegistrationAsync(string name, string password, Invitation invitation);
    public Task<Result<User, Error>> LoginAsync(string name, string password);
    public Task<Result<User, Error>> RegistrationWithPasswordProofAsync(string name, PasswordCredentials credentials, Invitation invitation);
}
