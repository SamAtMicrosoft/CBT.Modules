﻿using Microsoft.Build.Construction;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CBT.NuGet.Internal
{
    internal sealed class NuGetPropertyGenerator
    {
        /// <summary>
        /// The name of the 'ID' attribute in the NuGet packages.config.
        /// </summary>
        private const string NuGetPackagesConfigIdAttributeName = "id";

        /// <summary>
        /// The name of the &lt;package /&gt; element in th NuGet packages.config.
        /// </summary>
        private const string NuGetPackagesConfigPackageElementName = "package";

        /// <summary>
        /// The name of the 'Version' attribute in the NuGet packages.config.
        /// </summary>
        private const string NuGetPackagesConfigVersionAttributeName = "version";

        private readonly string[] _packageConfigPaths;

        public NuGetPropertyGenerator(params string[] packageConfigPaths)
        {
            if (packageConfigPaths == null)
            {
                throw new ArgumentNullException("packageConfigPaths");
            }

            _packageConfigPaths = packageConfigPaths;
        }

        public bool Generate(string outputPath, string propertyNamePrefix, string propertyValuePrefix)
        {
            ProjectRootElement project = ProjectRootElement.Create();

            ProjectPropertyGroupElement propertyGroup = project.AddPropertyGroup();

            propertyGroup.SetProperty("MSBuildAllProjects", "$(MSBuildAllProjects);$(MSBuildThisFileFullPath)");

            foreach (PackageInfo packageInfo in ParsePackages())
            {
                string propertyName = String.Format(CultureInfo.CurrentCulture, "{0}{1}", propertyNamePrefix, packageInfo.Id.Replace(".", "_"));
                string propertyValue = String.Format(CultureInfo.CurrentCulture, "{0}{1}.{2}", propertyValuePrefix, packageInfo.Id, packageInfo.VersionString);

                propertyGroup.SetProperty(propertyName, propertyValue);
            }

            project.Save(outputPath);

            return true;
        }

        private IEnumerable<PackageInfo> ParsePackages()
        {
            IDictionary<string, PackageInfo> packages = new Dictionary<string, PackageInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (string packageConfigPath in _packageConfigPaths.Where(i => !String.IsNullOrWhiteSpace(i) && File.Exists(i)))
            {
                XDocument document = XDocument.Load(packageConfigPath);

                if (document.Root != null)
                {
                    foreach (var item in document.Root.Elements(NuGetPackagesConfigPackageElementName).Select(i => new
                    {
                        Id = i.Attribute(NuGetPackagesConfigIdAttributeName) == null ? null : i.Attribute(NuGetPackagesConfigIdAttributeName).Value,
                        Version = i.Attribute(NuGetPackagesConfigVersionAttributeName) == null ? null : i.Attribute(NuGetPackagesConfigVersionAttributeName).Value,
                    }))
                    {
                        // Skip packages that are missing an 'id' or 'version' attribute or if they specified value is an empty string
                        //
                        if (item.Id == null || item.Version == null ||
                            String.IsNullOrWhiteSpace(item.Id) ||
                            String.IsNullOrWhiteSpace(item.Version))
                        {
                            continue;
                        }

                        PackageInfo packageInfo = new PackageInfo(item.Id, item.Version);

                        if (packages.ContainsKey(packageInfo.Id))
                        {
                            packages[packageInfo.Id] = packageInfo;
                        }
                        else
                        {
                            packages.Add(packageInfo.Id, packageInfo);
                        }
                    }
                }
            }

            return packages.Values;
        }
    }
}