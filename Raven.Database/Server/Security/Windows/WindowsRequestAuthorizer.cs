using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Principal;
using System.Threading;
using System.Web;
using System.Web.Http;
using Raven.Abstractions.Data;
using Raven.Database.Extensions;
using Raven.Database.Server.Abstractions;
using System.Linq;
using Raven.Abstractions.Extensions;
using Raven.Database.Server.Controllers;
using Raven.Database.Server.Tenancy;

namespace Raven.Database.Server.Security.Windows
{
	public class WindowsRequestAuthorizer : AbstractRequestAuthorizer
	{
		private List<WindowsAuthData> requiredGroups = new List<WindowsAuthData>();
		private List<WindowsAuthData> requiredUsers = new List<WindowsAuthData>();

		private static event Action WindowsSettingsChanged = delegate { };

		public static void InvokeWindowsSettingsChanged()
		{
			WindowsSettingsChanged();
		}

		protected override void Initialize()
		{
			WindowsSettingsChanged += UpdateSettings;
			UpdateSettings();
		}

		public void UpdateSettings()
		{
			var doc = server.SystemDatabase.Get("Raven/Authorization/WindowsSettings", null);

			if (doc == null)
			{
				requiredGroups = new List<WindowsAuthData>();
				requiredUsers = new List<WindowsAuthData>();
				return;
			}

			var required = doc.DataAsJson.JsonDeserialization<WindowsAuthDocument>();
			if (required == null)
			{
				requiredGroups = new List<WindowsAuthData>();
				requiredUsers = new List<WindowsAuthData>();
				return;
			}

			requiredGroups = required.RequiredGroups != null
								 ? required.RequiredGroups.Where(data => data.Enabled).ToList()
								 : new List<WindowsAuthData>();
			requiredUsers = required.RequiredUsers != null
								? required.RequiredUsers.Where(data => data.Enabled).ToList()
								: new List<WindowsAuthData>();
		}

		public bool Authorize(IHttpContext ctx, bool ignoreDb)
		{
			Action onRejectingRequest;
			var databaseName = TenantId ?? Constants.SystemDatabase;
			var userCreated = TryCreateUser(ctx, databaseName, out onRejectingRequest);
			if (server.SystemConfiguration.AnonymousUserAccessMode == AnonymousUserAccessMode.None && userCreated == false)
			{
				onRejectingRequest();
				return false;
			}

			PrincipalWithDatabaseAccess user = null;
			if (userCreated)
			{
				user = (PrincipalWithDatabaseAccess)ctx.User;
				CurrentOperationContext.Headers.Value[Constants.RavenAuthenticatedUser] = ctx.User.Identity.Name;
				CurrentOperationContext.User.Value = ctx.User;

				// admins always go through
				if (user.Principal.IsAdministrator(server.SystemConfiguration.AnonymousUserAccessMode))
					return true;

				// backup operators can go through
				if (user.Principal.IsBackupOperator(server.SystemConfiguration.AnonymousUserAccessMode))
					return true;
			}

			var httpRequest = ctx.Request;
			bool isGetRequest = IsGetRequest(httpRequest.HttpMethod, httpRequest.Url.AbsolutePath);
			switch (server.SystemConfiguration.AnonymousUserAccessMode)
			{
				case AnonymousUserAccessMode.Admin:
				case AnonymousUserAccessMode.All:
					return true; // if we have, doesn't matter if we have / don't have the user
				case AnonymousUserAccessMode.Get:
					if (isGetRequest)
						return true;
					goto case AnonymousUserAccessMode.None;
				case AnonymousUserAccessMode.None:
					if (userCreated)
					{
						if (user.AdminDatabases.Contains(databaseName) ||
							user.AdminDatabases.Contains("*") || ignoreDb)
							return true;
						if (user.ReadWriteDatabases.Contains(databaseName) ||
							user.ReadWriteDatabases.Contains("*"))
							return true;
						if (isGetRequest && (user.ReadOnlyDatabases.Contains(databaseName) ||
							user.ReadOnlyDatabases.Contains("*")))
							return true;
					}

					onRejectingRequest();
					return false;
				default:
					throw new ArgumentOutOfRangeException();
			}
		}

		public bool TryAuthorize(RavenApiController controller, bool ignoreDb, out HttpResponseMessage msg)
		{
			Func<HttpResponseMessage> onRejectingRequest;
			var databaseName = controller.DatabaseName ?? Constants.SystemDatabase;
			var userCreated = TryCreateUser(controller, databaseName, out onRejectingRequest);
			if (server.SystemConfiguration.AnonymousUserAccessMode == AnonymousUserAccessMode.None && userCreated == false)
			{
				msg = onRejectingRequest();
				return false;
			}

			PrincipalWithDatabaseAccess user = null;
			if (userCreated)
			{
				user = (PrincipalWithDatabaseAccess)controller.User;
				CurrentOperationContext.Headers.Value[Constants.RavenAuthenticatedUser] = controller.User.Identity.Name;
				CurrentOperationContext.User.Value = controller.User;

				// admins always go through
				if (user.Principal.IsAdministrator(server.SystemConfiguration.AnonymousUserAccessMode))
				{
					msg = controller.GetEmptyMessage();
					return true;
				}

				// backup operators can go through
				if (user.Principal.IsBackupOperator(server.SystemConfiguration.AnonymousUserAccessMode))
				{
					msg = controller.GetEmptyMessage();
					return true;
				}
			}

			bool isGetRequest = IsGetRequest(controller.InnerRequest.Method.Method, controller.InnerRequest.RequestUri.AbsolutePath);
			switch (server.SystemConfiguration.AnonymousUserAccessMode)
			{
				case AnonymousUserAccessMode.Admin:
				case AnonymousUserAccessMode.All:
					msg = controller.GetEmptyMessage();
					return true; // if we have, doesn't matter if we have / don't have the user
				case AnonymousUserAccessMode.Get:
					if (isGetRequest)
					{
						msg = controller.GetEmptyMessage();
						return true;
					}
					goto case AnonymousUserAccessMode.None;
				case AnonymousUserAccessMode.None:
					if (userCreated)
					{
						if (user.AdminDatabases.Contains(databaseName) ||
						    user.AdminDatabases.Contains("*") || ignoreDb)
						{
							msg = controller.GetEmptyMessage();
							return true;
						}
						if (user.ReadWriteDatabases.Contains(databaseName) ||
						    user.ReadWriteDatabases.Contains("*"))
						{
							msg = controller.GetEmptyMessage();
							return true;
						}
						if (isGetRequest && (user.ReadOnlyDatabases.Contains(databaseName) ||
						                     user.ReadOnlyDatabases.Contains("*")))
						{
							msg = controller.GetEmptyMessage();
							return true;
						}
					}

					msg = onRejectingRequest();
					return false;
				default:
					throw new ArgumentOutOfRangeException();
			}
		}

		private bool TryCreateUser(IHttpContext ctx, string databaseName, out Action onRejectingRequest)
		{
			var invalidUser = (ctx.User == null || ctx.User.Identity.IsAuthenticated == false);
			if (invalidUser)
			{
				onRejectingRequest = () =>
				{
					ProvideDebugAuthInfo(ctx, new
					{
						Reason = "User is null or not authenticated"
					});
					ctx.Response.AddHeader("Raven-Required-Auth", "Windows");
					if (string.IsNullOrEmpty(Settings.OAuthTokenServer) == false)
					{
						ctx.Response.AddHeader("OAuth-Source", Settings.OAuthTokenServer);
					}
					ctx.SetStatusToUnauthorized();
				};
				return false;
			}

			var dbUsersIaAllowedAccessTo = requiredUsers
				.Where(data => ctx.User.Identity.Name.Equals(data.Name, StringComparison.InvariantCultureIgnoreCase))
				.SelectMany(source => source.Databases)
				.Concat(requiredGroups.Where(data => ctx.User.IsInRole(data.Name)).SelectMany(x => x.Databases))
				.ToList();
			var user = UpdateUserPrincipal(ctx, dbUsersIaAllowedAccessTo);

			onRejectingRequest = () =>
			{
				ctx.SetStatusToForbidden();

				ProvideDebugAuthInfo(ctx, new
				{
					user.Identity.Name,
					user.AdminDatabases,
					user.ReadOnlyDatabases,
					user.ReadWriteDatabases,
					DatabaseName = databaseName
				});
			};
			return true;
		}

		private bool TryCreateUser(RavenApiController controller, string databaseName, out Func<HttpResponseMessage> onRejectingRequest)
		{
			var invalidUser = (controller.User == null || controller.User.Identity.IsAuthenticated == false);
			if (invalidUser)
			{
				onRejectingRequest = () =>
				{
					var msg = ProvideDebugAuthInfo(controller, new
					{
						Reason = "User is null or not authenticated"
					});
					controller.AddHeader("Raven-Required-Auth", "Windows", msg);
					if (string.IsNullOrEmpty(controller.DatabasesLandlord.SystemConfiguration.OAuthTokenServer) == false)
					{
						controller.AddHeader("OAuth-Source", controller.DatabasesLandlord.SystemConfiguration.OAuthTokenServer, msg);
					}
					msg.StatusCode = HttpStatusCode.Unauthorized;

					return msg;
				};
				return false;
			}

			var dbUsersIaAllowedAccessTo = requiredUsers
				.Where(data => controller.User.Identity.Name.Equals(data.Name, StringComparison.InvariantCultureIgnoreCase))
				.SelectMany(source => source.Databases)
				.Concat(requiredGroups.Where(data => controller.User.IsInRole(data.Name)).SelectMany(x => x.Databases))
				.ToList();
			var user = UpdateUserPrincipal(controller, dbUsersIaAllowedAccessTo);

			onRejectingRequest = () =>
			{
				var msg = ProvideDebugAuthInfo(controller, new
				{
					user.Identity.Name,
					user.AdminDatabases,
					user.ReadOnlyDatabases,
					user.ReadWriteDatabases,
					DatabaseName = databaseName
				});

				msg.StatusCode = HttpStatusCode.Forbidden;

				throw new HttpResponseException(msg);
			};
			return true;
		}

		private static void ProvideDebugAuthInfo(IHttpContext ctx, object msg)
		{
			string debugAuth = ctx.Request.QueryString["debug-auth"];
			if (debugAuth == null)
				return;

			bool shouldProvideDebugAuthInformation;
			if (bool.TryParse(debugAuth, out shouldProvideDebugAuthInformation) && shouldProvideDebugAuthInformation)
			{
				ctx.WriteJson(msg);
			}
		}

		private static HttpResponseMessage ProvideDebugAuthInfo(RavenApiController controller, object msg)
		{
			string debugAuth = controller.GetQueryStringValue("debug-auth");
			if (debugAuth == null)
				return controller.GetEmptyMessage();

			bool shouldProvideDebugAuthInformation;
			if (bool.TryParse(debugAuth, out shouldProvideDebugAuthInformation) && shouldProvideDebugAuthInformation)
			{
				return controller.GetMessageWithObject(msg);
			}

			return controller.GetEmptyMessage();
		}

		private PrincipalWithDatabaseAccess UpdateUserPrincipal(IHttpContext ctx, List<DatabaseAccess> databaseAccessLists)
		{
			var access = ctx.User as PrincipalWithDatabaseAccess;
			if (access != null)
				return access;

			var user = new PrincipalWithDatabaseAccess((WindowsPrincipal)ctx.User);

			foreach (var databaseAccess in databaseAccessLists)
			{
				if (databaseAccess.Admin)
					user.AdminDatabases.Add(databaseAccess.TenantId);
				else if (databaseAccess.ReadOnly)
					user.ReadOnlyDatabases.Add(databaseAccess.TenantId);
				else
					user.ReadWriteDatabases.Add(databaseAccess.TenantId);
			}

			ctx.User = user;
			return user;
		}

		private PrincipalWithDatabaseAccess UpdateUserPrincipal(RavenApiController controller, List<DatabaseAccess> databaseAccessLists)
		{
			var access = controller.User as PrincipalWithDatabaseAccess;
			if (access != null)
				return access;

			var user = new PrincipalWithDatabaseAccess((WindowsPrincipal)controller.User);

			foreach (var databaseAccess in databaseAccessLists)
			{
				if (databaseAccess.Admin)
					user.AdminDatabases.Add(databaseAccess.TenantId);
				else if (databaseAccess.ReadOnly)
					user.ReadOnlyDatabases.Add(databaseAccess.TenantId);
				else
					user.ReadWriteDatabases.Add(databaseAccess.TenantId);
			}

			controller.User = user;
			Thread.CurrentPrincipal = user;

			return user;
		}

		public List<string> GetApprovedDatabases(IPrincipal user)
		{
			var winUser = user as PrincipalWithDatabaseAccess;
			if (winUser == null)
				return new List<string>();

			var list = new List<string>();
			list.AddRange(winUser.AdminDatabases);
			list.AddRange(winUser.ReadOnlyDatabases);
			list.AddRange(winUser.ReadWriteDatabases);

			return list;
		}

		public override void Dispose()
		{
			WindowsSettingsChanged -= UpdateSettings;
		}

		public IPrincipal GetUser(IHttpContext ctx)
		{
			Action onRejectingRequest;
			var databaseName = TenantId ?? Constants.SystemDatabase;
			var userCreated = TryCreateUser(ctx, databaseName, out onRejectingRequest);
			return userCreated ? ctx.User : null;
		}

		public IPrincipal GetUser(RavenApiController controller)
		{
			Func<HttpResponseMessage> onRejectingRequest;
			var databaseName = controller.DatabaseName ?? Constants.SystemDatabase;
			var userCreated = TryCreateUser(controller, databaseName, out onRejectingRequest);
			if (userCreated == false)
				onRejectingRequest();
			return userCreated ? controller.User : null;
		}
	}
}
