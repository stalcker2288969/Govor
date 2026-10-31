using Govor.Domain.Models.Users;

namespace Govor.Contracts.Requests;

public sealed record SetPrivilegesRequest(UserPrivileges Privileges);
