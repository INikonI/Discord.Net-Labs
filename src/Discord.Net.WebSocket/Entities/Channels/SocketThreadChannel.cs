using Discord.Rest;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Model = Discord.API.Channel;
using ThreadMember = Discord.API.ThreadMember;
using MemberUpdates = Discord.API.Gateway.ThreadMembersUpdated;
using System.Collections.Concurrent;

namespace Discord.WebSocket
{
    /// <summary>
    ///     Represents a thread channel inside of a guild.
    /// </summary>
    [DebuggerDisplay(@"{DebuggerDisplay,nq}")]
    public class SocketThreadChannel : SocketTextChannel, IThreadChannel
    {
        /// <inheritdoc/>
        public ThreadType Type { get; private set; }

        /// <summary>
        ///     Gets the owner of the current thread.
        /// </summary>
        public SocketThreadUser Owner { get; private set; }

        /// <summary>
        ///     Gets the current users within this thread.
        /// </summary>
        public SocketThreadUser CurrentUser
            => Users.FirstOrDefault(x => x.Id == Discord.CurrentUser.Id);

        /// <inheritdoc/>
        public bool Joined { get; private set; }

        /// <summary>
        ///     <see langword="true"/> if this thread is private, otherwise <see langword="false"/>
        /// </summary>
        public bool IsPrivateThread
            => this.Type == ThreadType.PrivateThread;

        /// <summary>
        ///     Gets the parent channel this thread resides in.
        /// </summary>
        public SocketTextChannel ParentChannel { get; private set; }

        /// <inheritdoc/>
        public int MessageCount { get; private set; }

        /// <inheritdoc/>
        public int MemberCount { get; private set; }

        /// <inheritdoc/>
        public bool Archived { get; private set; }

        /// <inheritdoc/>
        public DateTimeOffset ArchiveTimestamp { get; private set; }

        /// <inheritdoc/>
        public ThreadArchiveDuration AutoArchiveDuration { get; private set; }

        /// <inheritdoc/>
        public bool Locked { get; private set; }

        /// <summary>
        ///     Gets a collection of cached users within this thread.
        /// </summary>
        public new IReadOnlyCollection<SocketThreadUser> Users =>
            _members.Values.ToImmutableArray();


        private ConcurrentDictionary<ulong, SocketThreadUser> _members;

        private string DebuggerDisplay => $"{Name} ({Id}, Thread)";

        private bool _usersDownloaded = false;

        private object _downloadLock = new object();

        internal SocketThreadChannel(DiscordSocketClient discord, SocketGuild guild, ulong id, SocketTextChannel parent)
            : base(discord, id, guild)
        {
            this.ParentChannel = parent;
            this._members = new ConcurrentDictionary<ulong, SocketThreadUser>();
        }

        internal new static SocketThreadChannel Create(SocketGuild guild, ClientState state, Model model)
        {
            var parent = (SocketTextChannel)guild.GetChannel(model.CategoryId.Value);
            var entity = new SocketThreadChannel(guild.Discord, guild, model.Id, parent);
            entity.Update(state, model);
            return entity;
        }

        internal override void Update(ClientState state, Model model)
        {
            base.Update(state, model);

            this.Type = (ThreadType)model.Type;
            this.MessageCount = model.MessageCount.GetValueOrDefault(-1);
            this.MemberCount = model.MemberCount.GetValueOrDefault(-1);
            
            if (model.ThreadMetadata.IsSpecified)
            {
                this.Archived = model.ThreadMetadata.Value.Archived;
                this.ArchiveTimestamp = model.ThreadMetadata.Value.ArchiveTimestamp;
                this.AutoArchiveDuration = (ThreadArchiveDuration)model.ThreadMetadata.Value.AutoArchiveDuration;
                this.Locked = model.ThreadMetadata.Value.Locked.GetValueOrDefault(false);
            }

            if (model.OwnerId.IsSpecified)
            {
                this.Owner = GetUser(model.OwnerId.Value);
            }

            this.Joined = model.ThreadMember.IsSpecified;
        }

        internal IReadOnlyCollection<SocketThreadUser> RemoveUsers(ulong[] users)
        {
            List<SocketThreadUser> threadUsers = new();

            foreach (var userId in users)
            {
                if (_members.TryRemove(userId, out var user))
                    threadUsers.Add(user);
            }

            return threadUsers.ToImmutableArray();
        }

        internal SocketThreadUser AddOrUpdateThreadMember(ThreadMember model, SocketGuildUser guildMember)
        {
            if (_members.TryGetValue(model.UserId.Value, out SocketThreadUser member))
                member.Update(model);
            else
            {
                member = SocketThreadUser.Create(this.Guild, this, model, guildMember);
                member.GlobalUser.AddRef();
                _members[member.Id] = member;
            }
            return member;
        }

        /// <inheritdoc />
        public new SocketThreadUser GetUser(ulong id)
        {
            var user = Users.FirstOrDefault(x => x.Id == id);
            return user;
        }

        /// <summary>
        ///     Gets all users inside this thread.
        /// </summary>
        /// <remarks>
        ///     If all users are not downloaded then this method will call <see cref="DownloadUsersAsync(RequestOptions)"/> and return the result.
        /// </remarks>
        /// <param name="options">The options to be used when sending the request.</param>
        /// <returns>A task representing the download operation.</returns>
        public async Task<IReadOnlyCollection<SocketThreadUser>> GetUsersAsync(RequestOptions options = null)
        {
            // download all users if we havent
            if (!_usersDownloaded)
            {
                await DownloadUsersAsync(options);
                this._usersDownloaded = true;
            }

            return this.Users;
        }


        /// <summary>
        ///     Downloads all users that have access to this thread.
        /// </summary>
        /// <param name="options">The options to be used when sending the request.</param>
        /// <returns>A task representing the asyncronous download operation.</returns>
        public async Task DownloadUsersAsync(RequestOptions options = null)
        {
            var users = await Discord.ApiClient.ListThreadMembersAsync(this.Id, options);

            lock (_downloadLock)
            {
                foreach (var threadMember in users)
                {
                    var guildUser = this.Guild.GetUser(threadMember.UserId.Value);

                    this.AddOrUpdateThreadMember(threadMember, guildUser);
                }
            }
        }
        
        internal new SocketThreadChannel Clone() => MemberwiseClone() as SocketThreadChannel;

        /// <inheritdoc/>
        public Task JoinAsync(RequestOptions options = null)
            => Discord.ApiClient.JoinThreadAsync(this.Id, options);

        /// <inheritdoc/>
        public Task LeaveAsync(RequestOptions options = null)
             => Discord.ApiClient.LeaveThreadAsync(this.Id, options);

        /// <summary>
        ///     Adds a user to this thread.
        /// </summary>
        /// <param name="user">The <see cref="IGuildUser"/> to add.</param>
        /// <param name="options">The options to be used when sending the request.</param>
        /// <returns>
        ///     A task that represents the asynchronous operation of adding a member to a thread. 
        /// </returns>
        public Task AddUserAsync(IGuildUser user, RequestOptions options = null)
            => Discord.ApiClient.AddThreadMemberAsync(this.Id, user.Id, options);

        /// <summary>
        ///     Removes a user from this thread.
        /// </summary>
        /// <param name="user">The <see cref="IGuildUser"/> to remove from this thread.</param>
        /// <param name="options">The options to be used when sending the request.</param>
        /// <returns>
        ///     A task that represents the asynchronous operation of removing a user from this thread. 
        /// </returns>
        public Task RemoveUserAsync(IGuildUser user, RequestOptions options = null)
            => Discord.ApiClient.RemoveThreadMemberAsync(this.Id, user.Id, options);


        /// <inheritdoc/>
        /// <remarks>
        ///     <b>This method is not supported in threads.</b>
        /// </remarks>
        public override Task AddPermissionOverwriteAsync(IRole role, OverwritePermissions permissions, RequestOptions options = null)
            => throw new NotImplementedException();

        /// <inheritdoc/>
        /// <remarks>
        ///     <b>This method is not supported in threads.</b>
        /// </remarks>
        public override Task AddPermissionOverwriteAsync(IUser user, OverwritePermissions permissions, RequestOptions options = null)
            => throw new NotImplementedException();

        /// <inheritdoc/>
        /// <remarks>
        ///     <b>This method is not supported in threads.</b>
        /// </remarks>
        public override Task<IInviteMetadata> CreateInviteAsync(int? maxAge = 86400, int? maxUses = null, bool isTemporary = false, bool isUnique = false, RequestOptions options = null)
            => throw new NotImplementedException();

        /// <inheritdoc/>
        /// <remarks>
        ///     <b>This method is not supported in threads.</b>
        /// </remarks>
        public override Task<IInviteMetadata> CreateInviteToApplicationAsync(ulong applicationId, int? maxAge, int? maxUses = null, bool isTemporary = false, bool isUnique = false, RequestOptions options = null)
            => throw new NotImplementedException();

        /// <inheritdoc/>
        /// <remarks>
        ///     <b>This method is not supported in threads.</b>
        /// </remarks>
        public override Task<IInviteMetadata> CreateInviteToStreamAsync(IUser user, int? maxAge, int? maxUses = null, bool isTemporary = false, bool isUnique = false, RequestOptions options = null)
            => throw new NotImplementedException();

        /// <inheritdoc/>
        /// <remarks>
        ///     <b>This method is not supported in threads.</b>
        /// </remarks>
        public override Task<RestWebhook> CreateWebhookAsync(string name, Stream avatar = null, RequestOptions options = null)
            => throw new NotImplementedException();

        /// <inheritdoc/>
        /// <remarks>
        ///     <b>This method is not supported in threads.</b>
        /// </remarks>
        public override Task<IReadOnlyCollection<IInviteMetadata>> GetInvitesAsync(RequestOptions options = null)
            => throw new NotImplementedException();

        /// <inheritdoc/>
        /// <remarks>
        ///     <b>This method is not supported in threads.</b>
        /// </remarks>
        public override OverwritePermissions? GetPermissionOverwrite(IRole role)
            => ParentChannel.GetPermissionOverwrite(role);

        /// <inheritdoc/>
        /// <remarks>
        ///     <b>This method is not supported in threads.</b>
        /// </remarks>
        public override OverwritePermissions? GetPermissionOverwrite(IUser user)
            => ParentChannel.GetPermissionOverwrite(user);

        /// <inheritdoc/>
        /// <remarks>
        ///     <b>This method is not supported in threads.</b>
        /// </remarks>
        public override Task<RestWebhook> GetWebhookAsync(ulong id, RequestOptions options = null)
            => throw new NotImplementedException();

        /// <inheritdoc/>
        /// <remarks>
        ///     <b>This method is not supported in threads.</b>
        /// </remarks>
        public override Task<IReadOnlyCollection<RestWebhook>> GetWebhooksAsync(RequestOptions options = null)
            => throw new NotImplementedException();

        /// <inheritdoc/>
        /// <remarks>
        ///     <b>This method is not supported in threads.</b>
        /// </remarks>
        public override Task ModifyAsync(Action<TextChannelProperties> func, RequestOptions options = null)
            => ThreadHelper.ModifyAsync(this, Discord, func, options);

        /// <inheritdoc/>
        /// <remarks>
        ///     <b>This method is not supported in threads.</b>
        /// </remarks>
        public override Task RemovePermissionOverwriteAsync(IRole role, RequestOptions options = null)
            => throw new NotImplementedException();

        /// <inheritdoc/>
        /// <remarks>
        ///     <b>This method is not supported in threads.</b>
        /// </remarks>
        public override Task RemovePermissionOverwriteAsync(IUser user, RequestOptions options = null)
            => throw new NotImplementedException();

        /// <inheritdoc/>
        /// <remarks>
        ///     <b>This method is not supported in threads.</b>
        /// </remarks>
        public override IReadOnlyCollection<Overwrite> PermissionOverwrites
            => throw new NotImplementedException();

        /// <inheritdoc/>
        /// <remarks>
        ///     <b>This method is not supported in threads.</b>
        /// </remarks>
        public override Task SyncPermissionsAsync(RequestOptions options = null)
            => throw new NotImplementedException();

        string IChannel.Name => this.Name;
    }
}
