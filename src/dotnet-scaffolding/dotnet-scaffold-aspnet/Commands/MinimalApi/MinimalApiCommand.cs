// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using Microsoft.DotNet.Scaffolding.Core.Scaffolders;
using Microsoft.DotNet.Scaffolding.Helpers.General;
using Microsoft.DotNet.Scaffolding.Helpers.Roslyn;
using Microsoft.DotNet.Scaffolding.Helpers.Services;
using Microsoft.DotNet.Scaffolding.Helpers.Steps;
using Microsoft.DotNet.Tools.Scaffold.AspNet.Commands.Common;
using Microsoft.DotNet.Tools.Scaffold.AspNet.Commands.MinimalApi;
using Microsoft.DotNet.Tools.Scaffold.AspNet.Commands.Settings;
using Microsoft.DotNet.Tools.Scaffold.AspNet.Helpers;
using Microsoft.Extensions.Logging;

namespace Microsoft.DotNet.Tools.Scaffold.AspNet.Commands.API.MinimalApi;

internal class MinimalApiCommand : ICommandWithSettings<MinimalApiSettings>
{
    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;

    public MinimalApiCommand(
        IFileSystem fileSystem,
        ILogger logger)
    {
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public async Task<int> ExecuteAsync(MinimalApiSettings settings, ScaffolderContext context)
    {
        if (!ValidateMinimalApiSettings(settings))
        {
            return -1;
        }

        //initialize MinimalApiModel
        _logger.LogInformation("Initializing scaffolding model...");
        var minimalApiModel = await GetMinimalApiModelAsync(settings);
        if (minimalApiModel is null)
        {
            _logger.LogError("An error occurred.");
            return -1;
        }
        else
        {
            context.Properties.Add(nameof(MinimalApiModel), minimalApiModel);
        }

        //Install packages and add a DbContext (if needed)
        if (minimalApiModel.DbContextInfo.EfScenario)
        {
            _logger.LogInformation("Installing packages...");
            await InstallPackagesAsync(settings);
            var dbContextProperties = AspNetDbContextHelper.GetDbContextProperties(minimalApiModel.DbContextInfo, minimalApiModel.ProjectInfo);
            if (dbContextProperties is not null)
            {
                context.Properties.Add(nameof(DbContextProperties), dbContextProperties);
            }

            var projectPath = minimalApiModel.ProjectInfo?.AppSettings?.Workspace()?.InputPath;
            var projectBasePath = Path.GetDirectoryName(projectPath);
            if (!string.IsNullOrEmpty(projectBasePath))
            {
                context.Properties.Add("BaseProjectPath", projectBasePath);
            }
        }

        //Update the project's Program.cs file
        _logger.LogInformation("Updating project...");
        var projectUpdateResult = await UpdateProjectAsync(minimalApiModel);
        if (projectUpdateResult)
        {
            _logger.LogInformation("Finished");
            return 0;
        }
        else
        {
            _logger.LogError("An error occurred.");
            return -1;
        }
    }

    private bool ValidateMinimalApiSettings(MinimalApiSettings commandSettings)
    {
        if (string.IsNullOrEmpty(commandSettings.Project) || !_fileSystem.FileExists(commandSettings.Project))
        {
            _logger.LogError("Missing/Invalid --project option.");
            return false;
        }

        if (string.IsNullOrEmpty(commandSettings.Model))
        {
            _logger.LogError("Missing/Invalid --model option.");
            return false;
        }

        if (!string.IsNullOrEmpty(commandSettings.DataContext) &&
            (string.IsNullOrEmpty(commandSettings.DatabaseProvider) || !PackageConstants.EfConstants.EfPackagesDict.ContainsKey(commandSettings.DatabaseProvider)))
        {
            commandSettings.DatabaseProvider = PackageConstants.EfConstants.SqlServer;
        }

        return true;
    }

    private async Task<bool> UpdateProjectAsync(MinimalApiModel minimalApiModel)
    {
        CodeModifierConfig? config = ProjectModifierHelper.GetCodeModifierConfig("minimalApiChanges.json", System.Reflection.Assembly.GetExecutingAssembly());
        config = EditConfigForMinimalApi(config, minimalApiModel);
        if (minimalApiModel.ProjectInfo.AppSettings is not null &&
            minimalApiModel.ProjectInfo.CodeService is not null &&
            minimalApiModel.ProjectInfo.CodeChangeOptions is not null &&
            config is not null)
        {
            var codeChangeOptions = new CodeChangeOptions()
            {
                IsMinimalApp = await ProjectModifierHelper.IsMinimalApp(minimalApiModel.ProjectInfo.CodeService),
                UsingTopLevelsStatements = await ProjectModifierHelper.IsUsingTopLevelStatements(minimalApiModel.ProjectInfo.CodeService),
                EfScenario = minimalApiModel.DbContextInfo.EfScenario
            };

            var projectModifier = new ProjectModifier(
                minimalApiModel.ProjectInfo.AppSettings.Workspace().InputPath ?? string.Empty,
                minimalApiModel.ProjectInfo.CodeService,
                _logger,
                config,
                codeChangeOptions);

            return await projectModifier.RunAsync();
        }

        return false;
    }

    private CodeModifierConfig? EditConfigForMinimalApi(CodeModifierConfig? configToEdit, MinimalApiModel minimalApiModel)
    {
        if (configToEdit is null)
        {
            return null;
        }

        var programCsFile = configToEdit.Files?.FirstOrDefault(x => !string.IsNullOrEmpty(x.FileName) && x.FileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase));
        var globalMethod = programCsFile?.Methods?.FirstOrDefault(x => x.Key.Equals("Global", StringComparison.OrdinalIgnoreCase)).Value;
        if (globalMethod is not null)
        {
            //only one change in here
            var addEndpointsChange = globalMethod?.CodeChanges?.FirstOrDefault();
            if (minimalApiModel.ProjectInfo.CodeChangeOptions is not null &&
                !minimalApiModel.ProjectInfo.CodeChangeOptions.UsingTopLevelsStatements &&
                addEndpointsChange is not null)
            {
                addEndpointsChange = DocumentBuilder.AddLeadingTriviaSpaces(addEndpointsChange, spaces: 12);
            }

            if (addEndpointsChange is not null &&
                !string.IsNullOrEmpty(addEndpointsChange.Block) &&
                !string.IsNullOrEmpty(minimalApiModel.EndpointsMethodName))
            {
                //formatting the endpoints method call onto "app.{0}()"
                addEndpointsChange.Block = string.Format(addEndpointsChange.Block, minimalApiModel.EndpointsMethodName);
            }
        }

        var dbContextInfo = minimalApiModel.DbContextInfo;
        configToEdit = AspNetDbContextHelper.AddDbContextChanges(dbContextInfo, configToEdit);
        return configToEdit;
    }

    private async Task<MinimalApiModel?> GetMinimalApiModelAsync(MinimalApiSettings settings)
    {
        var projectInfo = ClassAnalyzers.GetProjectInfo(settings.Project, _logger);
        if (projectInfo is null || projectInfo.CodeService is null)
        {
            return null;
        }

        //find and set --model class properties
        ModelInfo? modelInfo = null;
        var allClasses = await projectInfo.CodeService.GetAllClassSymbolsAsync();
        var modelClassSymbol = allClasses.FirstOrDefault(x => x.Name.Equals(settings.Model, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(settings.Model) || modelClassSymbol is null)
        {
            _logger.LogError($"Invalid --model '{settings.Model}' provided");
            return null;
        }
        else
        {
            modelInfo = ClassAnalyzers.GetModelClassInfo(modelClassSymbol);
        }

        var validateModelInfoResult = ClassAnalyzers.ValidateModelForCrudScaffolders(modelInfo, _logger);
        if (!validateModelInfoResult)
        {
            _logger.LogError($"Invalid --model '{settings.Model}'");
            return null;
        }

        //find DbContext info or create properties for a new one.
        var dbContextClassName = settings.DataContext;
        DbContextInfo dbContextInfo = new();

        if (!string.IsNullOrEmpty(dbContextClassName) && !string.IsNullOrEmpty(settings.DatabaseProvider))
        {
            var dbContextClassSymbol = allClasses.FirstOrDefault(x => x.Name.Equals(dbContextClassName, StringComparison.OrdinalIgnoreCase));
            dbContextInfo = ClassAnalyzers.GetDbContextInfo(dbContextClassSymbol, projectInfo.AppSettings, dbContextClassName, settings.DatabaseProvider, settings.Model);
        }

        MinimalApiModel scaffoldingModel = new()
        {
            ProjectInfo = projectInfo,
            ModelInfo = modelInfo,
            DbContextInfo = dbContextInfo
        };

        //find endpoints class name and path
        var allDocs = await projectInfo.CodeService.GetAllDocumentsAsync();
        scaffoldingModel.EndpointsMethodName = $"Map{modelClassSymbol.Name}Endpoints";
        if (!string.IsNullOrEmpty(settings.Endpoints))
        {
            scaffoldingModel.EndpointsFileName = StringUtil.EnsureCsExtension(settings.Endpoints);
            var existingEndpointsDoc = allDocs.FirstOrDefault(x => x.Name.Equals(scaffoldingModel.EndpointsFileName, StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith(scaffoldingModel.EndpointsFileName, StringComparison.OrdinalIgnoreCase));
            if (existingEndpointsDoc is not null)
            {
                scaffoldingModel.EndpointsPath = existingEndpointsDoc.FilePath ?? existingEndpointsDoc.Name;
            }
            else
            {
                scaffoldingModel.EndpointsClassName = Path.GetFileNameWithoutExtension(scaffoldingModel.EndpointsFileName);
                scaffoldingModel.EndpointsPath = CommandHelpers.GetNewFilePath(scaffoldingModel.ProjectInfo.AppSettings, scaffoldingModel.EndpointsFileName);
            }
        }
        else
        {
            scaffoldingModel.EndpointsFileName = $"{settings.Model}Endpoints.cs";
            scaffoldingModel.EndpointsClassName = $"{settings.Model}Endpoints";
            scaffoldingModel.EndpointsPath = CommandHelpers.GetNewFilePath(scaffoldingModel.ProjectInfo.AppSettings, scaffoldingModel.EndpointsFileName);
        }



        scaffoldingModel.OpenAPI = settings.OpenApi;
        if (scaffoldingModel.ProjectInfo is not null && scaffoldingModel.ProjectInfo.CodeService is not null)
        {
            scaffoldingModel.ProjectInfo.CodeChangeOptions = new CodeChangeOptions
            {
                IsMinimalApp = await ProjectModifierHelper.IsMinimalApp(scaffoldingModel.ProjectInfo.CodeService),
                UsingTopLevelsStatements = await ProjectModifierHelper.IsUsingTopLevelStatements(scaffoldingModel.ProjectInfo.CodeService),
                EfScenario = scaffoldingModel.DbContextInfo.EfScenario
            };
        }

        return scaffoldingModel;
    }

    private async Task InstallPackagesAsync(MinimalApiSettings commandSettings)
    {
        //add Microsoft.EntityFrameworkCore.Tools package regardless of the DatabaseProvider
        var packageList = new List<string>()
        {
            PackageConstants.EfConstants.EfToolsPackageName
        };

        if (!string.IsNullOrEmpty(commandSettings.DatabaseProvider) &&
           PackageConstants.EfConstants.EfPackagesDict.TryGetValue(commandSettings.DatabaseProvider, out string? projectPackageName))
        {
            packageList.Add(projectPackageName);
        }

        await new AddPackagesStep
        {
            PackageNames = packageList,
            ProjectPath = commandSettings.Project,
            Prerelease = commandSettings.Prerelease,
            Logger = _logger
        }.ExecuteAsync();
    }
}
