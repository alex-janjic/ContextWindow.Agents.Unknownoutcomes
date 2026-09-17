FROM mcr.microsoft.com/dotnet/sdk:10.0.100
WORKDIR /sample
COPY . .
RUN dotnet restore tests/ContextWindow.Agents.UnknownOutcomes.Tests/ContextWindow.Agents.UnknownOutcomes.Tests.csproj
CMD ["dotnet","test","tests/ContextWindow.Agents.UnknownOutcomes.Tests/ContextWindow.Agents.UnknownOutcomes.Tests.csproj","--logger","console;verbosity=detailed"]
