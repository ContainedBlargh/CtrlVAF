﻿using MFiles.VAF.Configuration;

using CtrlVAF.Core;

using MFilesAPI;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CtrlVAF.Models;

namespace CtrlVAF.Validators
{
    public class ValidatorDispatcher : Dispatcher<IEnumerable<ValidationFinding>>
    {
        private Vault Vault;
        private object Config;

        public ValidatorDispatcher(Vault vault, object config)
        {
            Vault = vault;
            Config = config;

            IncludeAssemblies(Assembly.GetCallingAssembly());
        }

        public override IEnumerable<ValidationFinding> Dispatch(params ICtrlVAFCommand[] commands)
        {
            var types = GetTypes(commands);

            return HandleConcreteTypes(types, commands);
        }

        protected internal override IEnumerable<Type> GetTypes(params ICtrlVAFCommand[] commands)
        {
            var configType = Config.GetType();

            // Attempt to get types from the cache
            if (TypeCache.TryGetValue(configType, out var cachedTypes))
            {
                return cachedTypes;
            }

            var concreteTypes = Assemblies.SelectMany(a => {
                return a
                .GetTypes()
                .Where(t =>
                    t.IsClass &&
                    t.GetInterfaces().Contains(typeof(ICustomValidator))
                    );
            }); 
            
            TypeCache.TryAdd(configType, concreteTypes);

            return concreteTypes;
        }

        protected internal override IEnumerable<ValidationFinding> HandleConcreteTypes(IEnumerable<Type> types, params ICtrlVAFCommand[] commands)
        {
            if (!types.Any())
                yield break;

            foreach (Type concreteType in types)
            {
                //Find config property (or sub-property) matching the generic argument of the basetype
                Type configSubType = concreteType.BaseType.GenericTypeArguments[0];

                var subConfig = GetConfigPropertyOfType(Config, configSubType);

                if (subConfig == null)
                    continue;

                var concreteHandler = Activator.CreateInstance(concreteType) as ICustomValidator;

                foreach (var finding in concreteHandler.Validate(Vault, subConfig))
                {
                    yield return finding;
                }
            }
            
        }

        private object GetConfigPropertyOfType(object config, Type configSubType)
        {
            if (config.GetType() == configSubType)
                return config;

            var configProperties = config.GetType().GetProperties();

            foreach (var configProperty in configProperties)
            {
                if (!configProperty.PropertyType.IsClass)
                    continue;

                var subConfig = configProperty.GetValue(config);

                if (configProperty.PropertyType == configSubType)
                    return subConfig;
            }

            foreach (var configProperty in configProperties)
            {
                if (!configProperty.PropertyType.IsClass)
                    continue;

                var subConfig = configProperty.GetValue(config);

                var subsubConfig = GetConfigPropertyOfType(subConfig, configSubType);
                if (subsubConfig == null)
                    continue;
                else
                    return subsubConfig;

            }

            return null;
        }
    }
}
