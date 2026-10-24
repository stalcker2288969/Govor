using AutoMapper;
using Govor.API.Controllers;
using Govor.API.Controllers.AdminStuff;
using Govor.Application.Infrastructure.Extensions;
using Govor.Application.Reactions;
using Govor.Contracts.Responses;
using Govor.Domain.Common;
using Govor.Domain.Models.Reactions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Govor.API.Tests.Unit.Controllers;

[TestFixture]
public class ReactionPackControllerTests
{
    private readonly Guid _user=Guid.NewGuid(),_pack=Guid.NewGuid(),_reaction=Guid.NewGuid();
    private Mock<IReactionPackService> _packs=null!;
    private Mock<IMapper> _mapper=null!;
    private ReactionPacksController _controller=null!;
    private ReactionPacksAdminController _admin=null!;
    private ReactionPackResponse _packResponse=null!;
    private ReactionItemResponse _itemResponse=null!;
    [SetUp] public void Setup()
    {
        _packs=new();_mapper=new();var user=new Mock<ICurrentUserService>();user.Setup(x=>x.GetCurrentUserId()).Returns(_user);
        _packResponse=new();_itemResponse=new();
        _mapper.Setup(x=>x.Map<ReactionPackResponse>(It.IsAny<object>())).Returns(_packResponse);
        _mapper.Setup(x=>x.Map<ReactionItemResponse>(It.IsAny<object>())).Returns(_itemResponse);
        _controller=new(_packs.Object,user.Object,_mapper.Object);
        _admin=new(_packs.Object,user.Object,_mapper.Object){ControllerContext=new(){HttpContext=new DefaultHttpContext()}};
    }
    [TestCase(false)] [TestCase(true)] public async Task ListsForwardPaginationAndVisibility(bool admin)
    {
        var list=new List<ReactionPack>();var mapped=new List<ReactionPackResponse>();
        _packs.Setup(x=>x.ListAsync(_user,false,admin,7,9)).ReturnsAsync(list);
        _mapper.Setup(x=>x.Map<List<ReactionPackResponse>>(list)).Returns(mapped);
        var result=admin?await _admin.List(7,9):await _controller.List(7,9);
        Assert.That(((OkObjectResult)result).Value,Is.SameAs(mapped));
        _packs.Verify(x=>x.ListAsync(_user,false,admin,7,9),Times.Once);
        _packs.Setup(x=>x.ListAsync(_user,true,false,4,6)).ReturnsAsync(list);
        Assert.That(((OkObjectResult)await _controller.Mine(4,6)).Value,Is.SameAs(mapped));
        _packs.Verify(x=>x.ListAsync(_user,true,false,4,6),Times.Once);
    }
    [TestCase(false)] [TestCase(true)] public async Task GetMapsSuccessfulPackOrPreservesFailure(bool failure)
    {
        _packs.Setup(x=>x.GetAsync(_pack,It.IsAny<bool>())).ReturnsAsync(failure?Result.Failure<ReactionPack>(Error.NotFound("missing","missing")):new ReactionPack());
        foreach(var result in new[]{await _controller.Get(_pack),await _admin.Get(_pack)})
            if(failure) Assert.That(((ObjectResult)result).StatusCode,Is.EqualTo(404));else Assert.That(((OkObjectResult)result).Value,Is.SameAs(_packResponse));
        _packs.Verify(x=>x.GetAsync(_pack,true),Times.Once);_packs.Verify(x=>x.GetAsync(_pack,false),Times.Once);
    }
    [Test] public async Task SharedAndReactionMapContracts()
    {
        _packs.Setup(x=>x.GetSharedAsync("share")).ReturnsAsync(new ReactionPack());_packs.Setup(x=>x.GetReactionAsync(_reaction)).ReturnsAsync(new ReactionItem());
        Assert.That(((OkObjectResult)await _controller.Shared("share")).Value,Is.SameAs(_packResponse));
        Assert.That(((OkObjectResult)await _controller.Reaction(_reaction)).Value,Is.SameAs(_itemResponse));
        Assert.That(_controller.Limits(),Is.TypeOf<OkObjectResult>());
    }
    [TestCase(false)] [TestCase(true)] public async Task SubscriptionsForwardDesiredState(bool subscribe)
    {
        _packs.Setup(x=>x.SubscribeAsync(_user,_pack,subscribe)).ReturnsAsync(SmartRes.Unit.Value);
        var result=subscribe?await _controller.Subscribe(_pack):await _controller.Unsubscribe(_pack);
        Assert.That(((StatusCodeResult)result).StatusCode,Is.EqualTo(204));_packs.Verify(x=>x.SubscribeAsync(_user,_pack,subscribe),Times.Once);
    }
    [Test] public async Task AdminMutationsForwardActorFieldsAndMapContracts()
    {
        _packs.Setup(x=>x.CreateAsync(_user,"name","desc")).ReturnsAsync(new ReactionPack());
        _packs.Setup(x=>x.UpdateAsync(_user,_pack,"new","changed",false)).ReturnsAsync(new ReactionPack());
        _packs.Setup(x=>x.DisableAsync(_user,_pack)).ReturnsAsync(SmartRes.Unit.Value);
        _packs.Setup(x=>x.AddEmojiAsync(_user,_pack,"emoji","👍")).ReturnsAsync(new ReactionItem());
        _packs.Setup(x=>x.DisableReactionAsync(_user,_pack,_reaction)).ReturnsAsync(SmartRes.Unit.Value);
        Assert.That(((OkObjectResult)await _admin.Create(new(){Name="name",Description="desc"})).Value,Is.SameAs(_packResponse));
        Assert.That(((OkObjectResult)await _admin.Update(_pack,new(){Name="new",Description="changed",IsEnabled=false})).Value,Is.SameAs(_packResponse));
        Assert.That(((StatusCodeResult)await _admin.Disable(_pack)).StatusCode,Is.EqualTo(204));
        Assert.That(((OkObjectResult)await _admin.Emoji(_pack,new(){Name="emoji",Emoji="👍"})).Value,Is.SameAs(_itemResponse));
        Assert.That(((StatusCodeResult)await _admin.DisableReaction(_pack,_reaction)).StatusCode,Is.EqualTo(204));
    }
    [TestCase(-1)] [TestCase(0)] [TestCase(2_097_153)] public async Task MediaRejectsMissingEmptyOrOversizedFile(int length)
    {
        var file=new Mock<IFormFile>();file.SetupGet(x=>x.Length).Returns(length);
        Assert.That(await _admin.Media(_pack,new(){File=length<0?null!:file.Object}),Is.TypeOf<BadRequestObjectResult>());_packs.VerifyNoOtherCalls();
    }
    [Test] public async Task MediaCopiesBytesAndPassesRequestCancellation()
    {
        using var stream=new MemoryStream([1,2,3]);using var cancellation=new CancellationTokenSource();_admin.HttpContext.RequestAborted=cancellation.Token;
        _packs.Setup(x=>x.AddMediaAsync(_user,_pack,"image",It.Is<byte[]>(b=>b.SequenceEqual(new byte[]{1,2,3})),cancellation.Token)).ReturnsAsync(new ReactionItem());
        var result=await _admin.Media(_pack,new(){Name="image",File=new FormFile(stream,0,3,"file","image.png")});
        Assert.That(((OkObjectResult)result).Value,Is.SameAs(_itemResponse));
        _packs.Verify(x=>x.AddMediaAsync(_user,_pack,"image",It.IsAny<byte[]>(),cancellation.Token),Times.Once);
    }
}
