using Govor.API.Common.Extensions;
using Govor.API.Common.Mapping;
using Govor.API.Filters;
using Govor.Application.Profiles;
using Govor.Application.Users.UserOnlineStatus;
using Govor.Contracts.DTOs;
using Govor.Contracts.Responses.SignalR;
using Govor.Domain.Common;
using Govor.Domain.Models.Users;
using Govor.Tests.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Claims;

namespace Govor.API.Tests.Unit.Common;

[TestFixture]
public class ErrorAndMappingTests
{
    [TestCase(ErrorType.NotFound, 404, "Not Found")]
    [TestCase(ErrorType.Validation, 400, "Validation Error")]
    [TestCase(ErrorType.Conflict, 409, "Conflict")]
    [TestCase(ErrorType.Unauthorized, 401, "Unauthorized")]
    [TestCase(ErrorType.Forbidden, 403, "Forbidden")]
    [TestCase(ErrorType.ServerError, 500, "Internal Server Error")]
    [TestCase(ErrorType.Failure, 400, "Bad Request")]
    public void ResultErrorsPreserveCodeDetailsAndFieldErrors(ErrorType type, int status, string title)
    {
        var fields = new Dictionary<string, string[]> { { "field", ["required"] } };
        var error = new Error("code", "detail", type, fields);
        var response = (ObjectResult)Result.Failure<int>(error).ToActionResult();
        var problem = (ProblemDetails)response.Value!;
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(status));
            Assert.That(problem.Title, Is.EqualTo(title));
            Assert.That(problem.Detail, Is.EqualTo("detail"));
            Assert.That(problem.Extensions["errorCode"], Is.EqualTo("code"));
            Assert.That(problem.Extensions["errors"], Is.SameAs(fields));
        });
        var hub = error.ToHubResult<int>();
        Assert.That((int)hub.Status, Is.EqualTo(status));
        Assert.That(hub.ErrorMessage, Is.EqualTo(type == ErrorType.ServerError ? "Internal server error." : "detail"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MappingActionsUseCurrentOnlineState(bool online)
    {
        var id = Guid.NewGuid();
        var store = new Mock<IOnlineUserStore>();
        store.Setup(x => x.IsOnline(id)).Returns(online);
        var user = new UserDto();
        new UserToUserDtoMappingAction(store.Object).Process(new User { Id = id }, user, null!);
        var profile = new UserProfileDto();
        new UserProfileToUserProfileDtoMappingAction(store.Object).Process(new UserProfile { Id = id }, profile, null!);
        Assert.That(user.IsOnline, Is.EqualTo(online));
        Assert.That(profile.IsOnline, Is.EqualTo(online));
        store.Verify(x => x.IsOnline(id), Times.Exactly(2));
    }
}


