﻿using System;
using System.Diagnostics;
using System.Reflection;
using System.Web;
using System.Web.Hosting;
using System.Web.Mvc;
using System.Web.Routing;

namespace WebSearcherWebRole
{

    public class WebSearcherApplication : HttpApplication
    {

        public const string PageTitle = ".Onion Searcher";

        public static bool IsRetailBuild()
        {
#if DEBUG
            return false;
#else
            return true;
#endif
        }

        private static string version;
        public static string GetVersion()
        {
            if (version == null)
            {
                version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            }
            return version;
        }

        protected void Application_Start()
        {
#if DEBUG
            Trace.TraceInformation("WebSearcherApplication.Application_Start v" + GetVersion() + " on " + Environment.OSVersion.ToString());
#else
            Trace.TraceInformation("WebSearcherApplication.Application_Start v" + GetVersion() + " on " + Environment.OSVersion.ToString());
#endif

            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            AreaRegistration.RegisterAllAreas();
            RouteConfig.RegisterRoutes(RouteTable.Routes);

            MvcHandler.DisableMvcResponseHeader = true;
        }

        protected void Application_PreSendRequestHeaders()
        {
            Response.Headers.Remove("Server"); // event statics content files thanks to runAllManagedModulesForAllRequests
            // Microsoft-HTTPAPI/2.0 Error 400 Bad Request - Invalid Hostname may still occure, fixed with a registry flag for httpd service
        }

        protected void Application_Error(Object sender, EventArgs e)
        {
            Exception ex = Server.GetLastError().GetBaseException();
            if (ex is HttpException && !ex.Message.Contains(" was not found on controller "))
            {
                Trace.TraceError("WebSearcherApplication.Application_Error : " + Server.GetLastError().GetBaseException().ToString());
#if DEBUG
                if (Debugger.IsAttached) { Debugger.Break(); }
#endif
            }
            else
            {
                Trace.TraceWarning("WebSearcherApplication.Application_Error unknow route : " + Request.Url);
            }
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                Trace.TraceError("WebSearcherApplication.CurrentDomain_UnhandledException : " + ex.GetBaseException().Message, ex);
            }
            else
            {
                Trace.TraceError("WebSearcherApplication.CurrentDomain_UnhandledException : NULL");
            }
#if DEBUG
            if (Debugger.IsAttached) { Debugger.Break(); }
#endif
        }

        protected void Application_Stop()
        {
            Trace.TraceWarning("WebSearcherApplication.Application_Stop : " + HostingEnvironment.ShutdownReason);
        }

    }
}
