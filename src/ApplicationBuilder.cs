using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Oxide.CompilerServices
{
    internal class ApplicationBuilder
    {
        private readonly IServiceCollection _services;
        private ConfigurationBuilder? _configuration;
        private IConfigurationRoot? _configRoot;

        public ApplicationBuilder()
        {
            _services = new ServiceCollection();
        }

        public ApplicationBuilder WithConfiguration(Action<IConfigurationBuilder> configure, Action<IConfiguration, IServiceCollection>? addconfigs = null)
        {
            _services.AddOptions();
            _configuration = new ConfigurationBuilder();
            configure(_configuration);
            _configRoot = _configuration.Build();
            addconfigs?.Invoke(_configRoot, _services);
            return this;
        }

        public ApplicationBuilder WithLogging(Action<ILoggingBuilder, IConfiguration?> configure)
        {
            _services.AddLogging(s => configure(s, _configRoot));
            return this;
        }

        public ApplicationBuilder WithServices(Action<IServiceCollection> services)
        {
            services?.Invoke(_services);
            return this;
        }

        public Application Build()
        {
            if (_configRoot != null)
            {
                _services.AddSingleton(_configRoot);
            }

            _services.AddSingleton<Application>();
            return _services.BuildServiceProvider().GetRequiredService<Application>();
        }
    }
}
