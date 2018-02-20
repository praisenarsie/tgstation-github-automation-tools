﻿using Cyberboss.AspNetCore.AsyncInitializer;
using Hangfire;
using Hangfire.SQLite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using TGWebhooks.Core.Configuration;
using TGWebhooks.Api;
using TGWebhooks.Core.Models;

namespace TGWebhooks.Core
{
	/// <summary>
	/// Startup point for the web application
	/// </summary>
#pragma warning disable CA1812
	sealed class Application
#pragma warning restore CA1812
	{
		/// <summary>
		/// The user agent string to provide to various APIs
		/// </summary>
		public const string UserAgent = "tgstation-github-automation-tools";

		/// <summary>
		/// The path to the directory the <see cref="Application"/> should use for data files
		/// </summary>
		public static string DataDirectory { get; private set; }

		/// <summary>
		/// The <see cref="IConfiguration"/> for the <see cref="Application"/>
		/// </summary>
		readonly IConfiguration configuration;

		/// <summary>
		/// Construct an <see cref="Application"/>
		/// </summary>
		/// <param name="_configuration">The value of <see cref="configuration"/></param>
		/// <param name="hostingEnvironment">The <see cref="IHostingEnvironment"/> used to determin the <see cref="DataDirectory"/></param>
		public Application(IConfiguration _configuration, IHostingEnvironment hostingEnvironment)
		{
			if (hostingEnvironment == null)
				throw new ArgumentNullException(nameof(hostingEnvironment));
			configuration = _configuration ?? throw new ArgumentNullException(nameof(_configuration));
			DataDirectory = Path.Combine(hostingEnvironment.ContentRootPath, "App_Data");
		}

		/// <summary>
		/// Configure dependency injected services
		/// </summary>
		/// <param name="services">The <see cref="IServiceCollection"/> to configure</param>
		public void ConfigureServices(IServiceCollection services)
		{
			services.AddHangfire(_ => _.UseSQLiteStorage(new SqliteConnectionStringBuilder
			{
				DataSource = Path.Combine(DataDirectory, "HangfireDatabase.sqlite3"),
				Mode = SqliteOpenMode.ReadWriteCreate
			}.ConnectionString));
			services.AddMvc();
			services.AddOptions();
			services.AddLocalization();

			services.Configure<GeneralConfiguration>(configuration.GetSection(GeneralConfiguration.Section));
			services.Configure<GitHubConfiguration>(configuration.GetSection(GitHubConfiguration.Section));
			services.Configure<TravisConfiguration>(configuration.GetSection(TravisConfiguration.Section));
			services.Configure<DatabaseConfiguration>(configuration.GetSection(DatabaseConfiguration.Section));

			services.AddDbContext<DatabaseContext>(ServiceLifetime.Singleton);
			services.AddSingleton<IDatabaseContext>(x => x.GetRequiredService<DatabaseContext>());

			services.AddSingleton<IPluginManager, PluginManager>();
			services.AddSingleton<IComponentProvider>(x => x.GetRequiredService<IPluginManager>());
			services.AddSingleton<IGitHubManager, GitHubManager>();
			services.AddSingleton<IRepository, Repository>();
			services.AddSingleton<IIOManager, DefaultIOManager>();
			services.AddSingleton<IWebRequestManager, WebRequestManager>();
			services.AddSingleton<IContinuousIntegration, TravisContinuousIntegration>();
			services.AddSingleton<IAutoMergeHandler, AutoMergeHandler>();
		}

#pragma warning disable CA1822 // Mark members as static
		/// <summary>
		/// Configure the <see cref="Application"/>
		/// </summary>
		/// <param name="app">The <see cref="IApplicationBuilder"/> to configure</param>
		/// <param name="env">The <see cref="IHostingEnvironment"/> of the <see cref="Application"/></param>
		public void Configure(IApplicationBuilder app, IHostingEnvironment env)
#pragma warning restore CA1822 // Mark members as static
		{
			app.ApplicationServices.GetRequiredService<IIOManager>().CreateDirectory(DataDirectory, CancellationToken.None).GetAwaiter().GetResult();

			if (env.IsDevelopment())
				app.UseDeveloperExceptionPage();

			var supportedCultures = new List<CultureInfo>
			{
				new CultureInfo("en")
			};

			var defaultLocale = app.ApplicationServices.GetRequiredService<IOptions<GeneralConfiguration>>().Value.DefaultLocale;
			CultureInfo defaultCulture;
			try
			{
				defaultCulture = supportedCultures.Where(x => x.Name == defaultLocale).First();
			}
			catch (InvalidOperationException e)
			{
				throw new InvalidOperationException(String.Format(CultureInfo.CurrentCulture, "Locale: {0} is not supported!", defaultLocale), e);
			}

			CultureInfo.CurrentCulture = defaultCulture;
			CultureInfo.CurrentUICulture = defaultCulture;

			var options = new RequestLocalizationOptions
			{
				DefaultRequestCulture = new RequestCulture(defaultCulture),
				SupportedCultures = supportedCultures,
				SupportedUICultures = supportedCultures,
			};
			app.UseRequestLocalization(options);
			
			app.UseAsyncInitialization<IPluginManager>((pluginManager, cancellationToken) => pluginManager.Initialize(cancellationToken));
			app.UseAsyncInitialization<IRepository>((repository, cancellationToken) => repository.Initialize(cancellationToken));

			app.UseHangfireServer();

			app.UseStaticFiles();

			app.UseMvc();
		}
	}
}