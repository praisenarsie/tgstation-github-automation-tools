﻿using Octokit;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TGWebhooks.Interface;

namespace TGWebhooks.PRTagger
{
	/// <summary>
	/// Auto labelling PR plugin
	/// </summary>
	public class TaggerPlugin : IPlugin, IPayloadHandler<PullRequestEventPayload>
	{
		/// <inheritdoc />
		public bool Enabled { get; set; }

		/// <inheritdoc />
		public string Name => "Pull Request Tagger";

		/// <inheritdoc />
		public string Description => "Automatically labels pull requests based on certain criteria";

		/// <inheritdoc />
		public IEnumerable<IMergeRequirement> MergeRequirements => Enumerable.Empty<IMergeRequirement>();

		/// <summary>
		/// The <see cref="IGitHubManager"/> for the <see cref="TaggerPlugin"/>
		/// </summary>
		IGitHubManager gitHubManager;

		/// <summary>
		/// Label a pull request
		/// </summary>
		/// <param name="payload">The <see cref="PullRequestEventPayload"/> for the pull request</param>
		/// <param name="oneCheckTags"><see langword="true"/> if additional tags should be contionally applied, <see langword="false"/> otherwise</param>
		/// <returns></returns>
		async Task TagPR(PullRequestEventPayload payload, bool oneCheckTags)
		{
			async Task<bool?> MergeableCheck()
			{
				//check if the PR is mergeable, if not, don't tag it
				bool? mergeable = payload.PullRequest.Mergeable;
				for (var I = 0; !mergeable.HasValue && I < 3; ++I)
				{
					await Task.Delay(I * 1000);
					mergeable = (await gitHubManager.GetPullRequest(payload.Repository, payload.PullRequest.Number)).Mergeable;
				}
				return mergeable;
			};

			var mergeableTask = MergeableCheck();
			var filesChanged = gitHubManager.GetPullRequestChangedFiles(payload.Repository, payload.PullRequest.Number);
			var currentLabelsTask = gitHubManager.GetIssueLabels(payload.Repository, payload.PullRequest.Number);

			var labelsToAdd = new List<string>();
			var labelsToRemove = new List<string>();

			var lowerTitle = payload.PullRequest.Title.ToLower(CultureInfo.CurrentCulture);

			if (lowerTitle.Contains("refactor"))
				labelsToAdd.Add("Refactor");
			if (lowerTitle.Contains("[dnm]"))
				labelsToAdd.Add("Do Not Merge");
			if (lowerTitle.Contains("[wip]"))
				labelsToAdd.Add("Work In Progress");
			if (lowerTitle.Contains("revert") || lowerTitle.Contains("removes"))
				labelsToAdd.Add("Revert/Removal");

			var mergeableCheck = await mergeableTask;
			if (mergeableCheck.HasValue)
				if (!mergeableCheck.Value)
					labelsToAdd.Add("Merge Conflict");
				else
					labelsToRemove.Add("Merge Conflict");

			var treeToLabelMappings = new Dictionary<string, string>
			{
				{ "_maps", "Map Edit" },
				{ "tools", "Tools" },
				{ "SQL" , "SQL" },
				{ ".github" , "GitHub" }
			};

			var addOnlyTreeToLabelMappings = new Dictionary<string, string>
			{
				{ "icons", "Sprites" },
				{ "sound", "Sounds" },
				{ "config" , "Config Update" },
				{ "code/controllers/configuration/entries" , "Config Update" },
				{ "tgui", "UI" }
			};

			foreach (var I in await filesChanged)
			{
				foreach (var J in treeToLabelMappings)
					if (I.FileName.StartsWith(J.Key))
						labelsToAdd.Add(J.Value);
					else
						labelsToRemove.Add(J.Value);
				foreach (var J in addOnlyTreeToLabelMappings)
					if (I.FileName.StartsWith(J.Key))
						labelsToAdd.Add(J.Value);
			}

			labelsToAdd.RemoveAll(x => labelsToRemove.Contains(x));
			
			var newLabels = new List<string>();
			foreach (var I in labelsToAdd)
				newLabels.Add(I);

			var currentLabels = new List<Label>(await currentLabelsTask);

			currentLabels.RemoveAll(x => labelsToRemove.Contains(x.Name) || labelsToAdd.Contains(x.Name));
			foreach (var I in currentLabels)
				newLabels.Add(I.Name);

			await gitHubManager.SetIssueLabels(payload.Repository, payload.PullRequest.Number, newLabels);
		}

		/// <inheritdoc />
		public Task Configure(ILogger logger, IRepository repository, IGitHubManager gitHubManager, IIOManager ioManager, CancellationToken cancellationToken)
		{
			this.gitHubManager = gitHubManager ?? throw new ArgumentNullException(nameof(gitHubManager));
			return Task.CompletedTask;
		}

		/// <inheritdoc />
		public IEnumerable<IPayloadHandler<TPayload>> GetPayloadHandlers<TPayload>() where TPayload : ActivityPayload
		{
			if (gitHubManager == null)
				throw new InvalidOperationException("Configure() wasn't called!");
			if (typeof(TPayload) == typeof(PullRequestEventPayload))
				yield return (IPayloadHandler<TPayload>)this;
		}

		/// <inheritdoc />
		public async Task ProcessPayload(PullRequestEventPayload payload)
		{
			switch (payload.Action)
			{
				case "opened":
					await TagPR(payload, true);
					break;
				case "synchronize":
					await TagPR(payload, false);
					break;
				default:
					throw new NotSupportedException();
			}
		}
	}
}