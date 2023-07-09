FROM mcr.microsoft.com/dotnet/runtime:7.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY ["CloudflareDNSLoadBalancer/CloudflareDNSLoadBalancer.csproj", "CloudflareDNSLoadBalancer/"]
RUN dotnet restore "CloudflareDNSLoadBalancer/CloudflareDNSLoadBalancer.csproj"
COPY . .
WORKDIR "/src/CloudflareDNSLoadBalancer"
RUN dotnet build "CloudflareDNSLoadBalancer.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "CloudflareDNSLoadBalancer.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CloudflareDNSLoadBalancer.dll"]
