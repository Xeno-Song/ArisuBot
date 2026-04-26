using ArisuBot.Core.Interfaces;
using ArisuBot.LLM.Gemini;
using ArisuBot.LLM.Monitoring;
using ArisuBot.LLM.Services;
using ArisuBot.LLM.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace ArisuBot.LLM.Extensions;

/// <summary>LLM 관련 DI 등록 확장 메서드.</summary>
public static class LLMServiceExtensions
{
    /// <summary>config에 따라 LLM 공급자와 캐시 관련 서비스를 DI 컨테이너에 등록한다. 현재: Gemini 고정.</summary>
    [ExcludeFromCodeCoverage(Justification = "DI 배선 코드 — 실 IServiceCollection 없이 단위 테스트 불가.")]
    public static IServiceCollection AddLLMProvider(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LLMOptions>(configuration.GetSection(LLMOptions.SectionName));
        services.Configure<GeminiOptions>(configuration.GetSection(GeminiOptions.SectionName));
        services.Configure<CacheOptions>(configuration.GetSection(CacheOptions.SectionName));

        // GeminiStreamClient: API 키 필요 → Options에서 팩토리 생성
        // IGeminiStreamClient + IGeminiCacheClient 양쪽으로 등록
        services.AddSingleton<GeminiStreamClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<GeminiOptions>>().Value;
            return new GeminiStreamClient(opts.ApiKey);
        });
        services.AddSingleton<IGeminiStreamClient>(sp => sp.GetRequiredService<GeminiStreamClient>());
        services.AddSingleton<IGeminiCacheClient>(sp => sp.GetRequiredService<GeminiStreamClient>());

        services.Configure<MonitorServerOptions>(configuration.GetSection(MonitorServerOptions.SectionName));

        // LlmTcpServer: Monitor/Dashboard Sidecar에 이벤트 전달. IHostedService로 TCP 서버 루프 관리.
        // ILlmMonitorServer + IProcessingEventEmitter + IHostedService 등록 — Sidecar 미연결 시 이벤트 drop
        services.AddSingleton<LlmTcpServer>();
        services.AddSingleton<ILlmMonitorServer>(sp => sp.GetRequiredService<LlmTcpServer>());
        services.AddSingleton<IProcessingEventEmitter>(sp => sp.GetRequiredService<LlmTcpServer>());
        services.AddHostedService(sp => sp.GetRequiredService<LlmTcpServer>());

        // Tool 상태 관리 — in-memory, 재시작 시 초기화
        services.AddSingleton<IToolStateService, ToolStateService>();

        // GeminiProvider
        services.AddSingleton<ILLMProvider, GeminiProvider>();

        // GeminiCacheManager: ILLMCacheManager + 구체 타입 모두 등록 (CleanupService가 구체 타입 주입)
        services.AddSingleton<GeminiCacheManager>();
        services.AddSingleton<ILLMCacheManager>(sp => sp.GetRequiredService<GeminiCacheManager>());

        // GeminiCacheCleanupService: 앱 시작/종료 시 캐시 정리
        services.AddHostedService<GeminiCacheCleanupService>();

        return services;
    }
}
