﻿using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Web.Mvc;
using System.Web.UI;
using NuGet;

namespace NuGetGallery
{
    public partial class ApiController : Controller
    {
        private const string NuGetCommandLinePackage = "NuGet.CommandLine";
        private readonly IPackageService packageSvc;
        private readonly IUserService userSvc;
        private readonly IPackageFileService packageFileSvc;
        private readonly INuGetExeDownloaderService nugetExeDownloaderSvc;

        public ApiController(IPackageService packageSvc,
                             IPackageFileService packageFileSvc,
                             IUserService userSvc,
                             INuGetExeDownloaderService nugetExeDownloaderSvc)
        {
            this.packageSvc = packageSvc;
            this.packageFileSvc = packageFileSvc;
            this.userSvc = userSvc;
            this.nugetExeDownloaderSvc = nugetExeDownloaderSvc;
        }

        [ActionName("GetPackageApi"), HttpGet] 
        public virtual ActionResult GetPackage(string id, string version)
        {
            // if the version is null, the user is asking for the latest version. Presumably they don't want pre release versions. 
            // The allow prerelease flag is ignored if both id and version are specified.
            var package = packageSvc.FindPackageByIdAndVersion(id, version, allowPrerelease: false);

            if (package == null)
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.NotFound, string.Format(CultureInfo.CurrentCulture, Strings.PackageWithIdAndVersionNotFound, id, version));

            packageSvc.AddDownloadStatistics(package,
                                             Request.UserHostAddress,
                                             Request.UserAgent);

            if (!string.IsNullOrWhiteSpace(package.ExternalPackageUrl))
                return Redirect(package.ExternalPackageUrl);
            else
            {
                return packageFileSvc.CreateDownloadPackageActionResult(package);
            }
        }

        [ActionName("GetNuGetExeApi"),
         HttpGet,
         OutputCache(VaryByParam = "none", Location = OutputCacheLocation.ServerAndClient, Duration = 600)]
        public virtual ActionResult GetNuGetExe()
        {
            return nugetExeDownloaderSvc.CreateNuGetExeDownloadActionnResult();
        }

        [ActionName("VerifyPackageKeyApi"), HttpGet]
        public virtual ActionResult VerifyPackageKey(string apiKey, string id, string version)
        {
            Guid parsedApiKey;
            if (!Guid.TryParse(apiKey, out parsedApiKey))
            {
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.BadRequest, string.Format(CultureInfo.CurrentCulture, Strings.InvalidApiKey, apiKey));
            }

            var user = userSvc.FindByApiKey(parsedApiKey);
            if (user == null)
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.Forbidden, string.Format(CultureInfo.CurrentCulture, Strings.ApiKeyNotAuthorized, "push"));

            if (!String.IsNullOrEmpty(id))
            {
                // If the id is present, then verify that the user has permission to push for the specific Id \ version combination.
                var package = packageSvc.FindPackageByIdAndVersion(id, version);
                if (package == null)
                    return new HttpStatusCodeWithBodyResult(HttpStatusCode.NotFound, string.Format(CultureInfo.CurrentCulture, Strings.PackageWithIdAndVersionNotFound, id, version));

                if (!package.IsOwner(user))
                    return new HttpStatusCodeWithBodyResult(HttpStatusCode.Forbidden, string.Format(CultureInfo.CurrentCulture, Strings.ApiKeyNotAuthorized, "push"));
            }

            return new EmptyResult();
        }

        [ActionName("PushPackageApi"), HttpPut]
        public virtual ActionResult CreatePackagePut(string apiKey)
        {
            return CreatePackageInternal(apiKey);
        }

        [ActionName("PushPackageApi"), HttpPost]
        public virtual ActionResult CreatePackagePost(string apiKey)
        {
            return CreatePackageInternal(apiKey);
        }

        private ActionResult CreatePackageInternal(string apiKey)
        {
            Guid parsedApiKey;
            if (!Guid.TryParse(apiKey, out parsedApiKey))
            {
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.BadRequest, string.Format(CultureInfo.CurrentCulture, Strings.InvalidApiKey, apiKey));
            }

            var user = userSvc.FindByApiKey(parsedApiKey);
            if (user == null)
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.Forbidden, String.Format(CultureInfo.CurrentCulture, Strings.ApiKeyNotAuthorized, "push"));

            var packageToPush = ReadPackageFromRequest();

            // Ensure that the user can push packages for this id.
            var packageRegistration = packageSvc.FindPackageRegistrationById(packageToPush.Id);
            if (packageRegistration != null)
            {
                if (!packageRegistration.IsOwner(user))
                {
                    return new HttpStatusCodeWithBodyResult(HttpStatusCode.Forbidden, String.Format(CultureInfo.CurrentCulture, Strings.ApiKeyNotAuthorized, "push"));
                }

                // Check if a particular Id-Version combination already exists. We eventually need to remove this check.
                bool packageExists = packageRegistration.Packages.Any(p => p.Version.Equals(packageToPush.Version.ToString(), StringComparison.OrdinalIgnoreCase));
                if (packageExists)
                {
                    return new HttpStatusCodeWithBodyResult(HttpStatusCode.Conflict,
                        String.Format(CultureInfo.CurrentCulture, Strings.PackageExistsAndCannotBeModified, packageToPush.Id, packageToPush.Version.ToString()));
                }
            }

            var package = packageSvc.CreatePackage(packageToPush, user);
            if (packageToPush.Id.Equals(NuGetCommandLinePackage, StringComparison.OrdinalIgnoreCase) && package.IsLatestStable)
            {
                // If we're pushing a new stable version of NuGet.CommandLine, update the extracted executable.
                nugetExeDownloaderSvc.UpdateExecutable(packageToPush);
            }

            return new HttpStatusCodeResult(201);
        }

        [ActionName("DeletePackageApi"), HttpDelete]
        public virtual ActionResult DeletePackage(string apiKey, string id, string version)
        {
            Guid parsedApiKey;
            if (!Guid.TryParse(apiKey, out parsedApiKey))
            {
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.BadRequest, string.Format(CultureInfo.CurrentCulture, Strings.InvalidApiKey, apiKey));
            }

            var user = userSvc.FindByApiKey(parsedApiKey);
            if (user == null)
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.Forbidden, string.Format(CultureInfo.CurrentCulture, Strings.ApiKeyNotAuthorized, "delete"));

            var package = packageSvc.FindPackageByIdAndVersion(id, version);
            if (package == null)
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.NotFound, string.Format(CultureInfo.CurrentCulture, Strings.PackageWithIdAndVersionNotFound, id, version));

            if (!package.IsOwner(user))
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.Forbidden, string.Format(CultureInfo.CurrentCulture, Strings.ApiKeyNotAuthorized, "delete"));

            packageSvc.MarkPackageUnlisted(package);
            return new EmptyResult();
        }

        [ActionName("PublishPackageApi"), HttpPost]
        public virtual ActionResult PublishPackage(string apiKey, string id, string version)
        {
            return new EmptyResult();
        }

        protected override void OnException(ExceptionContext filterContext)
        {
            filterContext.ExceptionHandled = true;
            var exception = filterContext.Exception;
            var request = filterContext.HttpContext.Request;
            filterContext.Result = new HttpStatusCodeWithBodyResult(HttpStatusCode.InternalServerError, exception.Message, request.IsLocal ? exception.StackTrace : exception.Message);
        }

        protected internal virtual IPackage ReadPackageFromRequest()
        {
            Stream stream;
            if (Request.Files.Count > 0)
            {
                // If we're using the newer API, the package stream is sent as a file.
                stream = Request.Files[0].InputStream;
            }
            else
                stream = Request.InputStream;

            return new ZipPackage(stream);
        }
    }
}