﻿using Microsoft.Build.Evaluation;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Utilities;

namespace CBT.NuGet.Internal
{
    /// <summary>
    /// A class for loading MSBuild projects and their project references.
    /// </summary>
    internal sealed class MSBuildProjectLoader
    {
        /// <summary>
        /// The name of the <ProjectReference /> item in MSBuild projects.
        /// </summary>
        private const string ProjectReferenceItemName = "ProjectReference";

        /// <summary>
        /// Stores the global properties to use when loading projects.
        /// </summary>
        private readonly IDictionary<string, string> _globalProperties;

        /// <summary>
        /// Stores the list of paths to the projects that are loaded.
        /// </summary>
        private readonly HashSet<string> _loadedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Stores the <see cref="ProjectLoadSettings"/> to use when loading projects.
        /// </summary>
        private readonly ProjectLoadSettings _projectLoadSettings;

        /// <summary>
        /// Stores the ToolsVersion to use when loading projects.
        /// </summary>
        private readonly string _toolsVersion;

        private readonly TaskLoggingHelper _log;

        /// <summary>
        /// Initializes a new instance of the MSBuildProjectLoader class.
        /// </summary>
        /// <param name="globalProperties">Specifies the global properties to use when loading projects.</param>
        /// <param name="toolsVersion">Specifies the ToolsVersion to use when loading projects.</param>
        /// <param name="projectLoadSettings">Specifies the <see cref="ProjectLoadSettings"/> to use when loading projects.</param>
        /// <param name="log"></param>
        public MSBuildProjectLoader(IDictionary<string, string> globalProperties, string toolsVersion, TaskLoggingHelper log, ProjectLoadSettings projectLoadSettings = ProjectLoadSettings.Default)
        {
            _globalProperties = globalProperties;
            _toolsVersion = toolsVersion;
            _projectLoadSettings = projectLoadSettings;
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// Gets or sets a value indicating if statistics should be collected.
        /// </summary>
        public bool CollectStats { get; set; } = true;

        /// <summary>
        /// Gets or sets a <see cref="Func{Project, Boolean}"/> that determines if a project is a traversal project.
        /// </summary>
        public Func<Project, bool> IsTraveralProject { get; set; } = project => String.Equals("true", project.GetPropertyValue("IsTraversal"));

        public MSBuildProjectLoaderStatistics Statistics { get; } = new MSBuildProjectLoaderStatistics();

        /// <summary>
        /// Gets or sets the item names that specify project references in a traversal project.  The default value is "ProjectFile".
        /// </summary>
        public string TraveralProjectFileItemName { get; set; } = "ProjectFile";

        /// <summary>
        /// Loads the specified projects and their references.
        /// </summary>
        /// <param name="projectPaths">An <see cref="IEnumerable{String}"/> containing paths to the projects to load.</param>
        /// <returns>A <see cref="ProjectCollection"/> object containing the loaded projects.</returns>
        public ProjectCollection LoadProjectsAndReferences(IEnumerable<string> projectPaths)
        {
            // Create a ProjectCollection for this thread
            //
            ProjectCollection projectCollection = new ProjectCollection(_globalProperties)
            {
                DefaultToolsVersion = _toolsVersion,
                DisableMarkDirty = true, // Not sure but hoping this improves load performance
            };

            Parallel.ForEach(projectPaths, projectPath => { LoadProject(projectPath, projectCollection, _projectLoadSettings); });

            return projectCollection;
        }

        /// <summary>
        /// Loads a single project if it hasn't already been loaded.
        /// </summary>
        /// <param name="projectPath">The path to the project.</param>
        /// <param name="projectCollection">A <see cref="ProjectCollection"/> to load the project into.</param>
        /// <param name="projectLoadSettings">Specifies the <see cref="ProjectLoadSettings"/> to use when loading projects.</param>
        private void LoadProject(string projectPath, ProjectCollection projectCollection, ProjectLoadSettings projectLoadSettings)
        {
            Project project;

            if (TryLoadProject(projectPath, projectCollection.DefaultToolsVersion, projectCollection, projectLoadSettings, out project))
            {
                LoadProjectReferences(project, _projectLoadSettings);
            }
        }

        /// <summary>
        /// Loads the project references of the specified project.
        /// </summary>
        /// <param name="project">The <see cref="Project"/> to load the references of.</param>
        /// <param name="projectLoadSettings">Specifies the <see cref="ProjectLoadSettings"/> to use when loading projects.</param>
        private void LoadProjectReferences(Project project, ProjectLoadSettings projectLoadSettings)
        {
            IEnumerable<ProjectItem> projects = project.GetItems(ProjectReferenceItemName);

            if (IsTraveralProject(project))
            {
                projects = projects.Concat(project.GetItems(TraveralProjectFileItemName));
            }

            Parallel.ForEach(projects, projectReferenceItem =>
            {
                string projectReferencePath = Path.IsPathRooted(projectReferenceItem.EvaluatedInclude) ? projectReferenceItem.EvaluatedInclude : Path.GetFullPath(Path.Combine(projectReferenceItem.Project.DirectoryPath, projectReferenceItem.EvaluatedInclude));

                LoadProject(projectReferencePath, projectReferenceItem.Project.ProjectCollection, projectLoadSettings);
            });
        }

        /// <summary>
        /// Attempts to load the specified project if it hasn't already been loaded.
        /// </summary>
        /// <param name="path">The path to the project to load.</param>
        /// <param name="toolsVersion">The ToolsVersion to use when loading the project.</param>
        /// <param name="projectCollection">The <see cref="ProjectCollection"/> to load the project into.</param>
        /// <param name="projectLoadSettings">Specifies the <see cref="ProjectLoadSettings"/> to use when loading projects.</param>
        /// <param name="project">Contains the loaded <see cref="Project"/> if one was loaded.</param>
        /// <returns><code>true</code> if the project was loaded, otherwise <code>false</code>.</returns>
        private bool TryLoadProject(string path, string toolsVersion, ProjectCollection projectCollection, ProjectLoadSettings projectLoadSettings, out Project project)
        {
            project = null;

            bool shouldLoadProject;

            lock (_loadedProjects)
            {
                shouldLoadProject = _loadedProjects.Add(Path.GetFullPath(path));
            }

            if (!shouldLoadProject)
            {
                return false;
            }

            long now = DateTime.Now.Ticks;

            try
            {
                project = new Project(path, null, toolsVersion, projectCollection, projectLoadSettings);
            }
            catch (InvalidProjectFileException e)
            {
                _log.LogError(null, e.ErrorCode, e.HelpKeyword, e.ProjectFile, e.LineNumber, e.ColumnNumber, e.EndLineNumber, e.EndColumnNumber, e.Message);

                return false;
            }
            catch (Exception e)
            {
                _log.LogErrorFromException(e);

                return false;
            }


            if (CollectStats)
            {
                Statistics.TryAddProjectLoadTime(path, TimeSpan.FromTicks(DateTime.Now.Ticks - now));
            }

            return true;
        }
    }

    /// <summary>
    /// Represents statistics of operations performed by the <see cref="MSBuildProjectLoader"/> class.
    /// </summary>
    public sealed class MSBuildProjectLoaderStatistics
    {
        private readonly ConcurrentDictionary<string, TimeSpan> _projectLoadTimes = new ConcurrentDictionary<string, TimeSpan>();

        internal MSBuildProjectLoaderStatistics()
        {
        }

        /// <summary>
        /// Gets an <see cref="IDictionary{String,TimeSpan}"/> of project load times.
        /// </summary>
        public IDictionary<string, TimeSpan> ProjectLoadTimes => _projectLoadTimes;

        internal bool TryAddProjectLoadTime(string path, TimeSpan timeSpan)
        {
            return _projectLoadTimes.TryAdd(path, timeSpan);
        }
    }
}