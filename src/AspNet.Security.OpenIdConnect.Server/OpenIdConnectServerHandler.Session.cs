/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/aspnet-contrib/AspNet.Security.OpenIdConnect.Server
 * for more information concerning the license and the contributors participating to this project.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AspNet.Security.OpenIdConnect.Primitives;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace AspNet.Security.OpenIdConnect.Server
{
    public partial class OpenIdConnectServerHandler : AuthenticationHandler<OpenIdConnectServerOptions>
    {
        private async Task<bool> InvokeLogoutEndpointAsync()
        {
            OpenIdConnectRequest request;

            // Note: logout requests must be made via GET but POST requests
            // are also accepted to allow flowing large logout payloads.
            // See https://openid.net/specs/openid-connect-session-1_0.html#RPLogout
            if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                request = new OpenIdConnectRequest(Request.Query);
            }

            else if (string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
                if (string.IsNullOrEmpty(Request.ContentType))
                {
                    Logger.LogError("The logout request was rejected because " +
                                    "the mandatory 'Content-Type' header was missing.");

                    return await SendLogoutResponseAsync(new OpenIdConnectResponse
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "The mandatory 'Content-Type' header must be specified."
                    });
                }

                // May have media/type; charset=utf-8, allow partial match.
                if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogError("The logout request was rejected because an invalid 'Content-Type' " +
                                    "header was specified: {ContentType}.", Request.ContentType);

                    return await SendLogoutResponseAsync(new OpenIdConnectResponse
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "The specified 'Content-Type' header is not valid."
                    });
                }

                request = new OpenIdConnectRequest(await Request.ReadFormAsync(Context.RequestAborted));
            }

            else
            {
                Logger.LogError("The logout request was rejected because an invalid " +
                                "HTTP method was specified: {Method}.", Request.Method);

                return await SendLogoutResponseAsync(new OpenIdConnectResponse
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "The specified HTTP method is not valid."
                });
            }

            // Note: set the message type before invoking the ExtractLogoutRequest event.
            request.SetProperty(OpenIdConnectConstants.Properties.MessageType,
                                OpenIdConnectConstants.MessageTypes.LogoutRequest);

            // Store the logout request in the ASP.NET context.
            Context.SetOpenIdConnectRequest(request);

            var @event = new ExtractLogoutRequestContext(Context, Options, request);
            await Options.Provider.ExtractLogoutRequest(@event);

            if (@event.HandledResponse)
            {
                Logger.LogDebug("The logout request was handled in user code.");

                return true;
            }

            else if (@event.Skipped)
            {
                Logger.LogDebug("The default logout request handling was skipped from user code.");

                return false;
            }

            else if (@event.IsRejected)
            {
                Logger.LogError("The logout request was rejected with the following error: {Error} ; {Description}",
                                /* Error: */ @event.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                                /* Description: */ @event.ErrorDescription);

                return await SendLogoutResponseAsync(new OpenIdConnectResponse
                {
                    Error = @event.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = @event.ErrorDescription,
                    ErrorUri = @event.ErrorUri
                });
            }

            Logger.LogInformation("The logout request was successfully extracted " +
                                  "from the HTTP request: {Request}.", request);

            var context = new ValidateLogoutRequestContext(Context, Options, request);
            await Options.Provider.ValidateLogoutRequest(context);

            if (context.HandledResponse)
            {
                Logger.LogDebug("The logout request was handled in user code.");

                return true;
            }

            else if (context.Skipped)
            {
                Logger.LogDebug("The default logout request handling was skipped from user code.");

                return false;
            }

            else if (context.IsRejected)
            {
                Logger.LogError("The logout request was rejected with the following error: {Error} ; {Description}",
                                /* Error: */ context.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                                /* Description: */ context.ErrorDescription);

                return await SendLogoutResponseAsync(new OpenIdConnectResponse
                {
                    Error = context.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = context.ErrorDescription,
                    ErrorUri = context.ErrorUri
                });
            }

            // Store the validated post_logout_redirect_uri as a request property.
            request.SetProperty(OpenIdConnectConstants.Properties.PostLogoutRedirectUri, context.PostLogoutRedirectUri);

            Logger.LogInformation("The logout request was successfully validated.");

            var notification = new HandleLogoutRequestContext(Context, Options, request);
            await Options.Provider.HandleLogoutRequest(notification);

            if (notification.HandledResponse)
            {
                Logger.LogDebug("The logout request was handled in user code.");

                return true;
            }

            else if (notification.Skipped)
            {
                Logger.LogDebug("The default logout request handling was skipped from user code.");

                return false;
            }

            else if (notification.IsRejected)
            {
                Logger.LogError("The logout request was rejected with the following error: {Error} ; {Description}",
                                /* Error: */ notification.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                                /* Description: */ notification.ErrorDescription);

                return await SendLogoutResponseAsync(new OpenIdConnectResponse
                {
                    Error = notification.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = notification.ErrorDescription,
                    ErrorUri = notification.ErrorUri
                });
            }

            Logger.LogError("The logout request was rejected because it was not handled by the user code.");

            return await SendLogoutResponseAsync(new OpenIdConnectResponse
            {
                Error = OpenIdConnectConstants.Errors.InvalidRequest,
                ErrorDescription = "The logout request was rejected by the authorization server."
            });
        }

        private async Task<bool> SendLogoutResponseAsync(OpenIdConnectResponse response)
        {
            var request = Context.GetOpenIdConnectRequest();
            Context.SetOpenIdConnectResponse(response);

            response.SetProperty(OpenIdConnectConstants.Properties.MessageType,
                                 OpenIdConnectConstants.MessageTypes.LogoutResponse);

            // Note: as this stage, the request may be null (e.g if it couldn't be extracted from the HTTP request).
            var notification = new ApplyLogoutResponseContext(Context, Options, request, response)
            {
                PostLogoutRedirectUri = request?.GetProperty<string>(OpenIdConnectConstants.Properties.PostLogoutRedirectUri)
            };

            await Options.Provider.ApplyLogoutResponse(notification);

            if (notification.HandledResponse)
            {
                Logger.LogDebug("The logout request was handled in user code.");

                return true;
            }

            else if (notification.Skipped)
            {
                Logger.LogDebug("The default logout request handling was skipped from user code.");

                return false;
            }

            if (!string.IsNullOrEmpty(response.Error))
            {
                // Apply a 400 status code by default.
                Response.StatusCode = 400;

                if (Options.ApplicationCanDisplayErrors)
                {
                    // Return false to allow the rest of
                    // the pipeline to handle the request.
                    return false;
                }

                Logger.LogInformation("The logout response was successfully returned " +
                                      "as a plain-text document: {Response}.", response);

                return await SendNativePageAsync(response);
            }

            // Don't redirect the user agent if no explicit post_logout_redirect_uri was
            // provided or if the URI was not fully validated by the application code.
            if (string.IsNullOrEmpty(notification.PostLogoutRedirectUri))
            {
                Logger.LogInformation("The logout response was successfully returned: {Response}.", response);

                return true;
            }

            // At this stage, throw an exception if the request was not properly extracted,
            if (request == null)
            {
                throw new InvalidOperationException("The logout response cannot be returned.");
            }

            // Attach the request state to the end session response.
            if (string.IsNullOrEmpty(response.State))
            {
                response.State = request.State;
            }

            // Create a new parameters dictionary holding the name/value pairs.
            var parameters = new Dictionary<string, string>();

            foreach (var parameter in response.GetParameters())
            {
                // Ignore null or empty parameters, including JSON
                // objects that can't be represented as strings.
                var value = (string) parameter.Value;
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                parameters.Add(parameter.Key, value);
            }

            Logger.LogInformation("The logout response was successfully returned to '{PostLogoutRedirectUri}': {Response}.",
                                  notification.PostLogoutRedirectUri, response);

            var location = QueryHelpers.AddQueryString(notification.PostLogoutRedirectUri, parameters);

            Response.Redirect(location);
            return true;
        }
    }
}
