﻿using CtrlVAF.Core;
using CtrlVAF.Models;
using CtrlVAF.Validation;

using MFiles.VAF.Common;
using MFiles.VAF.Extensions.MultiServerMode;
using MFiles.VAF.MultiserverMode;

using Newtonsoft.Json;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace CtrlVAF.BackgroundOperations
{
    public class BackgroundDispatcher<TConfig> : Dispatcher where TConfig : class, new()
    {
        private readonly Core.ConfigurableVaultApplicationBase<TConfig> vaultApplication;

        public BackgroundDispatcher(Core.ConfigurableVaultApplicationBase<TConfig> vaultApplication)
        {
            this.vaultApplication = vaultApplication;
        }

        public override void Dispatch(params ICtrlVAFCommand[] commands)
        {
            IncludeAssemblies(Assembly.GetCallingAssembly());

            var concreteTypes = GetTypes();

            if (!concreteTypes.Any())
                return;

            HandleConcreteTypes(concreteTypes);
        }

        

        protected internal override IEnumerable<Type> GetTypes(params ICtrlVAFCommand[] commands)
        {
            IncludeAssemblies(typeof(TConfig));

            var concreteTypes = Assemblies.SelectMany(a =>
            {
                return a.GetTypes().Where(t =>
                {
                    return t.IsClass &&
                           t.BaseType.IsGenericType &&
                           t.BaseType.GetGenericTypeDefinition() == typeof(BackgroundTaskHandler<,>) &&
                           t.IsDefined(typeof(BackgroundOperationAttribute));
                });
            });

            return concreteTypes;
        }

        protected internal override void HandleConcreteTypes(IEnumerable<Type> concreteTypes, params ICtrlVAFCommand[] commands)
        {
            List<string> PermanentBackgroundOperationNames = new List<string>();
            List<string> OnDemandBackgroundOperationNames = new List<string>();

            foreach (Type concreteType in concreteTypes)
            {
                

                BackgroundOperationAttribute operationInfo = concreteType.GetCustomAttribute<BackgroundOperationAttribute>();

                if (concreteType.IsDefined(typeof(RecurringAttribute)))
                {
                    var attr = concreteType.GetCustomAttribute<RecurringAttribute>();

                    TimeSpan interval = TimeSpan.FromMinutes(attr.IntervalInMinutes);

                    TaskQueueBackgroundOperation operation = vaultApplication.TaskQueueBackgroundOperationManager.StartRecurringBackgroundOperation(
                        operationInfo.Name,
                        interval,
                        (job, directive) => 
                        {
                            var backgroundTaskHandler = GetTaskHandler(concreteType);

                            var taskMethod = backgroundTaskHandler.GetType().GetMethod(nameof(IBackgroundTaskHandler<object, EmptyTQD>.Task));

                            try
                            {
                                taskMethod.Invoke(backgroundTaskHandler, new object[] { job, directive });
                            }
                            catch (TargetInvocationException te)
                            {
                                ExceptionDispatchInfo.Capture(te.InnerException).Throw();
                            }
                            catch (Exception e)
                            {
                                throw e;
                            }
                        }
                        );

                    vaultApplication.RecurringBackgroundOperations.AddBackgroundOperation(operationInfo.Name, operation, interval);

                    PermanentBackgroundOperationNames.Add(concreteType.FullName);
                }
                else
                {
                    TaskQueueBackgroundOperation operation = vaultApplication.TaskQueueBackgroundOperationManager.CreateBackgroundOperation<TaskQueueDirective>(
                        operationInfo.Name,
                        (job, directive) => 
                        {
                            var backgroundTaskHandler = GetTaskHandler(concreteType);

                            var taskMethod = backgroundTaskHandler.GetType().GetMethod(nameof(IBackgroundTaskHandler<object, EmptyTQD>.Task));

                            try
                            {
                                taskMethod.Invoke(backgroundTaskHandler, new object[] { job, directive });
                            }
                            catch (TargetInvocationException te)
                            {
                                ExceptionDispatchInfo.Capture(te.InnerException).Throw();
                            }
                            catch (Exception e)
                            {
                                throw e;
                            }
                        }
                        );

                    vaultApplication.OnDemandBackgroundOperations.AddBackgroundOperation(operationInfo.Name, operation);

                    OnDemandBackgroundOperationNames.Add(concreteType.FullName);
                }
            }

            string message = "";

            if (PermanentBackgroundOperationNames.Any())
                message += $"Permanent background operation classes: " + Environment.NewLine +
                    JsonConvert.SerializeObject(PermanentBackgroundOperationNames, Formatting.Indented) + Environment.NewLine;

            if (OnDemandBackgroundOperationNames.Any())
                message += $"On demand background operation classes: " + Environment.NewLine +
                    JsonConvert.SerializeObject(OnDemandBackgroundOperationNames, Formatting.Indented) + Environment.NewLine;

            SysUtils.ReportInfoToEventLog(
                $"{vaultApplication.GetType().Name} - BackgroundOperations",
                message
                );

            return;
        }

        private BackgroundTaskHandler GetTaskHandler(Type concreteType)
        {
            var backgroundTaskHandler = Activator.CreateInstance(concreteType) as BackgroundTaskHandler;

            //Get the right configuration subType and object
            TConfig config = vaultApplication.GetConfig();

            Type subConfigType = concreteType.BaseType.GenericTypeArguments[0];

            object subConfig = Dispatcher_Helpers.GetConfigSubProperty(config, subConfigType);

            //Set the configuration
            var configProperty = backgroundTaskHandler.GetType().GetProperty(nameof(IBackgroundTaskHandler<object, EmptyTQD>.Configuration));
            configProperty.SetValue(backgroundTaskHandler, subConfig);

            //Set the configuration independent variables
            backgroundTaskHandler.PermanentVault = vaultApplication.PermanentVault;
            backgroundTaskHandler.OnDemandBackgroundOperations = vaultApplication.OnDemandBackgroundOperations;
            backgroundTaskHandler.RecurringBackgroundOperations = vaultApplication.RecurringBackgroundOperations;
            if (vaultApplication.ValidationResults.TryGetValue(subConfigType, out ValidationResults results))
                backgroundTaskHandler.ValidationResults = results;
            else
                backgroundTaskHandler.ValidationResults = null;

            return backgroundTaskHandler;
        }

        private class EmptyTQD : TaskQueueDirective
        {

        }

    }
}
