﻿using Byond.TopicSender;
using Cyberboss.AspNetCore.AsyncInitializer;
using Hangfire;
using Hangfire.MySql;
using Hangfire.SQLite;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using TGWebhooks.Configuration;
using TGWebhooks.Modules;
using TGWebhooks.Models;

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
			configuration = _configuration ?? throw new ArgumentNullException(nameof(_configuration));
			DataDirectory = Path.Combine(hostingEnvironment?.ContentRootPath ?? throw new ArgumentNullException(nameof(hostingEnvironment)), "App_Data");
		}

		/// <summary>
		/// Configure dependency injected services
		/// </summary>
		/// <param name="services">The <see cref="IServiceCollection"/> to configure</param>
		public void ConfigureServices(IServiceCollection services)
		{
			if (services == null)
				throw new ArgumentNullException(nameof(services));

			var dbConfigSection = configuration.GetSection(DatabaseConfiguration.Section);

			services.Configure<GeneralConfiguration>(configuration.GetSection(GeneralConfiguration.Section));
			services.Configure<GitHubConfiguration>(configuration.GetSection(GitHubConfiguration.Section));
			services.Configure<TravisConfiguration>(configuration.GetSection(TravisConfiguration.Section));
			services.Configure<ServerConfiguration>(configuration.GetSection(ServerConfiguration.Section));
			services.Configure<DatabaseConfiguration>(dbConfigSection);

			services.Configure<MvcOptions>(options => options.Filters.Add(new RequireHttpsAttribute()));

			services.AddHangfire((builder) => DatabaseContext.SelectDatabaseType(dbConfigSection.Get<DatabaseConfiguration>(),
				x => builder.UseSqlServerStorage(x, new SqlServerStorageOptions { PrepareSchemaIfNecessary = true }),
				x => builder.UseStorage(new MySqlStorage(x, new MySqlStorageOptions { PrepareSchemaIfNecessary = true })),
				x => builder.UseSQLiteStorage(x, new SQLiteStorageOptions { PrepareSchemaIfNecessary = true })
			));
			services.AddLocalization(options => options.ResourcesPath = "Resources/Localizations");
			services.AddMvc();
			services.AddOptions();

			services.AddDbContext<DatabaseContext>(ServiceLifetime.Singleton);

			services.AddSingleton<IDatabaseContext>(x => x.GetRequiredService<DatabaseContext>());
			services.AddSingleton(typeof(IDataStoreFactory<>), typeof(DataStoreFactory<>));
			services.AddSingleton<IModuleManager, ModuleManager>();
			services.AddSingleton<IComponentProvider>(x => x.GetRequiredService<IModuleManager>());
			services.AddSingleton<IGitHubClientFactory, GitHubClientFactory>();
			services.AddSingleton<IGitHubManager, GitHubManager>();
			services.AddSingleton<IRepository, Repository>();
			services.AddSingleton<IIOManager, DefaultIOManager>();
			services.AddSingleton<IWebRequestManager, WebRequestManager>();
			services.AddSingleton<IContinuousIntegration, TravisContinuousIntegration>();
			services.AddSingleton<IAutoMergeHandler, AutoMergeHandler>();
			services.AddSingleton<IByondTopicSender, ByondTopicSender>();	//note the send/recieve timeouts are configured by the GameAnnouncerModule
			//I'll probably hate myself for that later

			services.AddModules();
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
			if (app == null)
				throw new ArgumentNullException(nameof(app));
			if (env == null)
				throw new ArgumentNullException(nameof(env));
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
				SupportedCultures = supportedCultures,
				SupportedUICultures = supportedCultures,
			};
			app.UseRequestLocalization(options);
			
			app.UseAsyncInitialization<IModuleManager>((pluginManager, cancellationToken) => pluginManager.Initialize(cancellationToken));
			app.UseAsyncInitialization<IRepository>((repository, cancellationToken) => repository.Initialize(cancellationToken));

			app.UseHangfireServer();

			app.UseStaticFiles();

			app.UseRewriter(new RewriteOptions().AddRedirectToHttpsPermanent());

			app.UseMvc();
		}
	}
}
