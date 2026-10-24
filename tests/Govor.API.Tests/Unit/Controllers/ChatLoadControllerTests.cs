using AutoMapper;
using Govor.API.Controllers;
using Govor.Application.Messages;
using Govor.Contracts.Requests;
using Govor.Contracts.Responses;
using Govor.Domain.Common;
using Govor.Domain.Models.Messages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.API.Tests.Unit.Controllers;

[TestFixture]
public class ChatLoadControllerTests
{
    private Mock<IMessagesLoader> _loader = null!;
    private Mock<IMapper> _mapper = null!;
    private ChatLoadController _controller = null!;
    private readonly Guid _user = Guid.NewGuid(), _chat = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _loader = new Mock<IMessagesLoader>();
        _mapper = new Mock<IMapper>();
        var current = new Mock<ICurrentUserService>();
        current.Setup(c => c.GetCurrentUserId()).Returns(_user);
        _controller = new(NullLogger<ChatLoadController>.Instance, _loader.Object, current.Object, _mapper.Object);
    }

    private Task<IActionResult> Load(bool group, MessageQuery query) =>
        group ? _controller.GetGroupMessages(_chat, query) : _controller.GetUserMessages(_chat, query);

    [TestCase(false)]
    [TestCase(true)]
    public async Task SuccessfulPageIsMappedAndReturnsOk(bool group)
    {
        var messages = new List<Message> { new() { Id = Guid.NewGuid() } };
        var response = new List<MessageResponse>();
        _mapper.Setup(m => m.Map<List<MessageResponse>>(messages)).Returns(response);
        _loader.Setup(l => l.LoadMessagesInChatGroup(_chat, _user, null, 20, 2)).ReturnsAsync(messages);
        _loader.Setup(l => l.LoadMessagesInUserChat(_chat, _user, null, 20, 2)).ReturnsAsync(messages);
        Assert.That(((OkObjectResult)await Load(group, new MessageQuery { Before = 20, After = 2 })).Value, Is.SameAs(response));
    }

    [TestCase(false, -1, 0)]
    [TestCase(true, 0, -1)]
    [TestCase(false, 101, 0)]
    [TestCase(true, 0, 101)]
    [TestCase(false, 60, 41)]
    [TestCase(true, 60, 41)]
    public async Task InvalidPageLimitsDoNotCallLoader(bool group, int before, int after)
    {
        Assert.That(await Load(group, new MessageQuery { Before = before, After = after }), Is.TypeOf<BadRequestObjectResult>());
        _loader.VerifyNoOtherCalls();
    }

    [TestCase(false, ErrorType.Forbidden, 403)]
    [TestCase(true, ErrorType.Forbidden, 403)]
    [TestCase(false, ErrorType.NotFound, 404)]
    [TestCase(true, ErrorType.Validation, 400)]
    public async Task ServiceFailureReturnsProblemDetails(bool group, ErrorType type, int status)
    {
        var failure = Result.Failure<List<Message>>(new Error("Chat.Failed", "failure", type));
        _loader.Setup(l => l.LoadMessagesInChatGroup(_chat, _user, null, 20, 2)).ReturnsAsync(failure);
        _loader.Setup(l => l.LoadMessagesInUserChat(_chat, _user, null, 20, 2)).ReturnsAsync(failure);
        var response = (ObjectResult)await Load(group, new MessageQuery { Before = 20, After = 2 });
        Assert.That(response.StatusCode, Is.EqualTo(status));
        Assert.That(((ProblemDetails)response.Value!).Extensions["errorCode"], Is.EqualTo("Chat.Failed"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UnexpectedExceptionReturns500(bool group)
    {
        _loader.Setup(l => l.LoadMessagesInChatGroup(_chat, _user, null, 20, 2)).ThrowsAsync(new Exception("database unavailable"));
        _loader.Setup(l => l.LoadMessagesInUserChat(_chat, _user, null, 20, 2)).ThrowsAsync(new Exception("database unavailable"));
        Assert.That(((ObjectResult)await Load(group, new MessageQuery { Before = 20, After = 2 })).StatusCode, Is.EqualTo(500));
    }
}
