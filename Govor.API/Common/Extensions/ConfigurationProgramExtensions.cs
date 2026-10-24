using Govor.API.Common.Mapping;
using Govor.API.Common.SignalR.Helpers;
using Govor.API.Hubs.Infrastructure;
using Govor.Application.Authentication;
using Govor.Application.Authentication.JWT;
using Govor.Application.Friends;
using Govor.Application.Groups;
using Govor.Application.Infrastructure.AdminsStuff;
using Govor.Application.Infrastructure.Common;
using Govor.Application.Infrastructure.Extensions;
using Govor.Application.Infrastructure.Validators;
using Govor.Application.Medias;
using Govor.Application.Messages;
using Govor.Application.Reactions;
using Govor.Application.PingHandler;
using Govor.Application.PrivateUserChats;
using Govor.Application.Profiles;
using Govor.Application.PushNotifications;
using Govor.Application.PushNotifications.Providers;
using Govor.Application.Storage;
using Govor.Application.Synching;
using Govor.Application.Users;
using Govor.Application.Users.UserOnlineStatus;
using Govor.Application.Users.UserSessions;
using Govor.Application.Users.UserSessions.Crypto;
using Govor.Domain;
using Microsoft.EntityFrameworkCore;

namespace Govor.API.Common.Extensions;

public static class ConfigurationProgramExtensions
{
    public static void AddServices(this IServiceCollection services)
    {
        services.AddScoped<Govor.Application.E2ee.E2eeService>();
        services.AddSingleton<PasswordChallenges>();
        services.AddScoped<PasswordAccountService>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<IUsernameValidator, UsernameValidator>();
        services.AddSingleton<IJwtTokenHasher, JwtTokenHasher>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IAccountService, AuthService>();
        services.AddScoped<IUsersAdministration, UsersService>();
        services.AddScoped<IInvitesService, InvitesService>();
        services.AddScoped<IInvitationGetter, InvitationGetter>();
        services.AddScoped<IInvitationGenerator, InvitationGenerator>();
        services.AddScoped<ISynchingService, SynchingService>();
        
        services.AddScoped<INowDateTimeProvider, NowDateTimeProvider>();
        
        // Friends services 
        services.AddScoped<IFriendshipService, FriendshipService>();
        services.AddScoped<IFriendRequestCommandService, FriendRequestCommandService>();
        services.AddScoped<IFriendRequestQueryService, FriendRequestQueryService>();
        services.AddScoped<IFriendsBlockService, FriendsBlockService>();
        
        services.AddScoped<IStorageService>(sp =>
        {
            var env = sp.GetRequiredService<IWebHostEnvironment>();
            return new LocalStorageService(env.ContentRootPath);
        });
        
        services.AddHttpContextAccessor(); // it's very important for CurrentUserService
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<ICurrentUserSessionService, CurrentUserSessionService>();
        
        services.AddMemoryCache();
        services.AddScoped<IPingHandlerService, PingHandlerService>();
        
        services.AddScoped<IUserGroupsGetterService, UserGroupsGetterService>();
        services.AddScoped<IGroupManagementService, GroupManagementService>();
        services.AddScoped<IGroupAvatarService, GroupAvatarService>();
        
        //services.AddScoped<IMessageCommandService, MessageCommandService>();
        services.AddScoped<IMessageSendingService, MessageSendingService>();
        services.AddScoped<IMessageReadingService, MessageReadingService>();
        services.AddScoped<IMessageReactionService, MessageReactionService>();
        services.AddScoped<IReactionPackService, ReactionPackService>();
        services.AddSingleton<IReactionMediaProcessor, ReactionMediaProcessor>();
        services.AddScoped<IMessageEditingService, MessageEditingService>();
        services.AddScoped<IMessageRemovingService, MessageRemovingService>();
        services.AddScoped<IVerifyFriendship, VerifyFriendship>();
        services.AddScoped<IUserPrivateChatsGetterService, UserPrivateChatsGetter>();
        services.AddScoped<IUserPrivateChatsCreator, UserPrivateChatsCreator>();
        services.AddScoped<IMessagesLoader, MessagesLoader>();
        services.AddScoped<IMediaService, MediaService>();
        services.AddScoped<IAccesserToDownloadMedia, AccesserToDownloadMediaService>();
       
        // User
        services.AddScoped<IUserNameExistValidator, UserNameExistValidator>();
        
        // UserSession
        services.AddScoped<IUserSessionOpener, UserSessionOpener>();
        services.AddScoped<IUserSessionRefresher, UserSessionRefresher>();
        
        services.AddScoped<IUserNotificationScopeService, UserNotificationScopeService>();
        services.AddScoped<IUserPresenceReader, UserPresenceReader>();
        services.AddSingleton<IOnlineUserStore, OnlineUserStore>();
        
        // Hubs Infrastructure
        services.AddScoped<IPrivateChatGroupManager, PrivateChatGroupManager>();
        services.AddScoped<IChatNotificationService, ChatNotificationService>();
        services.AddScoped<ChatPushDispatcher>();
        services.AddHostedService<ChatPushWorker>();
        services.AddScoped<IConnectionManager, ConnectionManager>();
        
        services.AddSingleton<IConnectionStore, ConnectionStore>();
            
        // Pushs
        services.AddScoped<IPushTokenService, PushTokenService>();
        services.AddScoped<IPushNotificationService, PushNotificationService>();
        services.AddSingleton<IPushNotificationProvider>(sp =>
            sp.GetRequiredService<IConfiguration>().GetValue("Firebase:Enabled", true)
                ? ActivatorUtilities.CreateInstance<FirebasePushProvider>(sp)
                : new NullPushProvider());
        
        
        // Auto Mapper 
        services.AddAutoMapper(op => { }, typeof(MappingProfile));

        services.AddScoped<IHubUserAccessor, HubUserAccessor>();

        services.AddScoped<IUserSessionReader, UserSessionReader>();
        services.AddScoped<IUserSessionRevoker, UserSessionRevoker>();

        services.AddScoped<ISessionKeyAttacher, SessionKeyAttacher>();
        services.AddScoped<ISessionKeysReader, SessionKeysReader>();
        services.AddScoped<IOneTimePreKeysRotator, OneTimePreKeysRotator>();
        
        services.AddScoped<IProfileService, ProfileService>();
    }

    public static void AddGovorDbContext(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<GovorDbContext>(options =>
        {
            options.UseNpgsql(
                configuration.GetConnectionString(nameof(GovorDbContext)),
                npgsqlOptions =>
                {
                    // retry for transient failures
                    npgsqlOptions.EnableRetryOnFailure(
                        5,
                        TimeSpan.FromSeconds(5),
                        null);
                });

            //options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);

            if (configuration.GetValue<bool>("Database:EnableDetailedErrors"))
                options.EnableDetailedErrors();
        });
    }
}
