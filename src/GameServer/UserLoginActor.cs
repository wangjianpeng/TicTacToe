﻿using System;
using System.Net;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Cluster.Utility;
using Akka.Interfaced;
using Akka.Interfaced.LogFilter;
using Common.Logging;
using Domain.Data;
using Domain.Interface;
using MongoDB.Driver;
using TrackableData;
using TrackableData.MongoDB;

namespace GameServer
{
    [Log]
    [ResponsiveException(typeof(ResultException))]
    public class UserLoginActor : InterfacedActor, IUserLogin
    {
        private readonly ILog _logger;
        private readonly ClusterNodeContext _clusterContext;
        private readonly IActorRef _clientSession;

        public UserLoginActor(ClusterNodeContext clusterContext,
                              IActorRef clientSession, EndPoint clientRemoteEndPoint)
        {
            _logger = LogManager.GetLogger($"UserLoginActor({clientRemoteEndPoint})");
            _clusterContext = clusterContext;
            _clientSession = clientSession;
        }

        [MessageHandler]
        private void OnMessage(ActorBoundSessionMessage.SessionTerminated message)
        {
            Context.Stop(Self);
        }

        private class AccountUserMapInfo
        {
            public string Id;
            public long UserId;
        }

        async Task<LoginResult> IUserLogin.Login(string id, string password, IUserEventObserver observer)
        {
            if (id == null)
                throw new ResultException(ResultCodeType.Argument, nameof(id));
            if (password == null)
                throw new ResultException(ResultCodeType.Argument, nameof(password));
            if (observer == null)
                throw new ResultException(ResultCodeType.Argument, nameof(observer));

            // Check account

            var accountId = await Authenticator.AuthenticateAsync(id, password);
            if (string.IsNullOrEmpty(accountId))
                throw new ResultException(ResultCodeType.LoginFailedIncorrectPassword);

            // Load user context from DB.
            // If no context, create new one.

            long userId;
            TrackableUserContext userContext;

            var accountUserMap = MongoDbStorage.Instance.Database.GetCollection<AccountUserMapInfo>("AccountUserMap");
            var userMap = await accountUserMap.Find(a => a.Id == accountId).FirstOrDefaultAsync();
            if (userMap != null)
            {
                userId = userMap.UserId;
                userContext = (TrackableUserContext)await MongoDbStorage.UserContextMapper.LoadAsync(
                    MongoDbStorage.Instance.UserCollection,
                    userId);

                userContext.SetDefaultTracker();
                userContext.Data.LoginCount += 1;
                userContext.Data.LastLoginTime = DateTime.UtcNow;

                await MongoDbStorage.UserContextMapper.SaveAsync(MongoDbStorage.Instance.UserCollection,
                                                                 userContext.Tracker, userId);
                userContext.Tracker.Clear();
            }
            else
            {
                var created = CreateUser(accountId);
                userId = created.Item1;
                userContext = created.Item2;
                await MongoDbStorage.UserContextMapper.CreateAsync(
                    MongoDbStorage.Instance.UserCollection,
                    userContext, userId);
                await accountUserMap.InsertOneAsync(new AccountUserMapInfo { Id = accountId, UserId = userId });
                userContext.SetDefaultTracker();
            }

            // Make UserActor

            IActorRef user;
            try
            {
                user = Context.System.ActorOf(
                    Props.Create<UserActor>(_clusterContext, _clientSession, userId, userContext, observer),
                    "user_" + userId);
            }
            catch (Exception e)
            {
                _logger.Error($"Exception in creating UserActor({userId}", e);
                throw new ResultException(ResultCodeType.InternalError);
            }

            // Register User in UserTable

            var registered = false;
            for (int i = 0; i < 10; i++)
            {
                var reply = await _clusterContext.UserTableContainer.Ask<DistributedActorTableMessage<long>.AddReply>(
                    new DistributedActorTableMessage<long>.Add(userId, user));
                if (reply.Added)
                {
                    registered = true;
                    break;
                }
                await Task.Delay(200);
            }
            if (registered == false)
            {
                user.Tell(InterfacedPoisonPill.Instance);
                throw new ResultException(ResultCodeType.LoginFailedAlreadyConnected);
            }

            // Bind user actor with client session, which makes client to communicate with this actor.

            var reply2 = await _clientSession.Ask<ActorBoundSessionMessage.BindReply>(
                new ActorBoundSessionMessage.Bind(user, typeof(IUser), null));
            if (reply2.ActorId == 0)
            {
                user.Tell(InterfacedPoisonPill.Instance);
                _logger.Error($"Failed in binding UserActor({userId}");
                throw new ResultException(ResultCodeType.InternalError);
            }

            return new LoginResult
            {
                UserId = userId,
                User = BoundActorRef.Create<UserRef>(reply2.ActorId),
                UserContext = userContext
            };
        }

        private Tuple<long, TrackableUserContext> CreateUser(string accountId)
        {
            var userId = UniqueInt64Id.GenerateNewId();
            var userContext = new TrackableUserContext
            {
                Data = new TrackableUserData
                {
                    Name = accountId.ToUpper(),
                    RegisterTime = DateTime.UtcNow,
                    LastLoginTime = DateTime.UtcNow,
                    LoginCount = 1,
                },
                Achivements = new TrackableDictionary<int, UserAchievement>()
            };
            return Tuple.Create(userId, userContext);
        }
    }
}
