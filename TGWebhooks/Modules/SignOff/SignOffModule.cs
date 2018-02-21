﻿using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TGWebhooks.Modules;

namespace TGWebhooks.Modules.SignOff
{
	/// <summary>
	/// <see cref="IModule"/> containing the Maintainer Sign Off <see cref="IMergeRequirement"/>
	/// </summary>
	sealed class SignOffModule : IModule, IMergeRequirement, IPayloadHandler<PullRequestEventPayload>
	{
		/// <summary>
		/// The key in <see cref="dataStore"/> where <see cref="PullRequestSignOffs"/>s are stored
		/// </summary>
		const string SignOffDataKey = "Signoffs";

		/// <inheritdoc />
		public Guid Uid => new Guid("bde81200-a275-4e93-b855-13865f3629fe");

		/// <inheritdoc />
		public string Name => "Maintainer Sign Off";

		/// <inheritdoc />
		public string Description => "Merge requirement of having a maintainer approve the 'idea' of a Pull Request. Sign offs are automatically dissmissed if the pull request body or title changes";

		/// <inheritdoc />
		public IEnumerable<IMergeRequirement> MergeRequirements => new List<IMergeRequirement> { this };

		/// <inheritdoc />
		public IEnumerable<IMergeHook> MergeHooks => Enumerable.Empty<IMergeHook>();

		/// <summary>
		/// The <see cref="IGitHubManager"/> for the <see cref="SignOffModule"/>
		/// </summary>
		readonly IGitHubManager gitHubManager;
		/// <summary>
		/// The <see cref="IDataStore"/> for the <see cref="SignOffModule"/>
		/// </summary>
		readonly IDataStore<SignOffModule> dataStore;
		
		/// <inheritdoc />
		public SignOffModule(IGitHubManager gitHubManager, IDataStore<SignOffModule> dataStore)
		{
			this.gitHubManager = gitHubManager ?? throw new ArgumentNullException(nameof(gitHubManager));
			this.dataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
		}

		/// <inheritdoc />
		public async Task<AutoMergeStatus> EvaluateFor(PullRequest pullRequest, CancellationToken cancellationToken)
		{
			if (pullRequest == null)
				throw new ArgumentNullException(nameof(pullRequest));
			var signOff = await dataStore.ReadData<PullRequestSignOffs>(SignOffDataKey, cancellationToken).ConfigureAwait(false);

			var result = new AutoMergeStatus() { RequiredProgress = 1 };
			if (signOff.Entries.TryGetValue(pullRequest.Number, out List<string> signers) && signers.Count > 0)
			{
				result.Progress = signers.Count;
				result.Notes.AddRange(signers);
			}
			else
				result.Notes.Add("No maintainers have signed off on this pull request");
			return result;
		}

		/// <inheritdoc />
		public IEnumerable<IPayloadHandler<TPayload>> GetPayloadHandlers<TPayload>() where TPayload : ActivityPayload
		{
			if (typeof(TPayload) == typeof(PullRequestEventPayload))
				yield return (IPayloadHandler<TPayload>)(object)this;
		}

		/// <inheritdoc />
		public Task Initialize(CancellationToken cancellationToken) => Task.CompletedTask;

		/// <inheritdoc />
		public async Task ProcessPayload(PullRequestEventPayload payload, CancellationToken cancellationToken)
		{
			if (payload == null)
				throw new ArgumentNullException(nameof(payload));
			
			if(payload.Action != "edited")
				throw new NotSupportedException();

			var signOff = await dataStore.ReadData<PullRequestSignOffs>(SignOffDataKey, cancellationToken).ConfigureAwait(false);

			if (!signOff.Entries.Remove(payload.PullRequest.Number))
				return;

			await dataStore.WriteData(SignOffDataKey, signOff, cancellationToken).ConfigureAwait(false);

			var botLoginTask = gitHubManager.GetUserLogin(null, cancellationToken);
			var reviews = await gitHubManager.GetPullRequestReviews(payload.PullRequest).ConfigureAwait(false);
			var botLogin = await botLoginTask.ConfigureAwait(false);
			foreach(var I in reviews)
				if (I.User.Id == botLogin.Id && I.State.Value == PullRequestReviewState.Approved)
					await gitHubManager.DismissReview(payload.PullRequest, I, "Sign off nullified due to edit of original post").ConfigureAwait(false);
		}
	}
}
