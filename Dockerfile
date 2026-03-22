FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY OPTool-Reloaded.slnx .
COPY src/OPTool/OPTool.csproj src/OPTool/
COPY lib/FiestaLib-Reloaded/src/FiestaLibReloaded.Networking/FiestaLibReloaded.Networking.csproj lib/FiestaLib-Reloaded/src/FiestaLibReloaded.Networking/
COPY lib/FiestaLib-Reloaded/src/FiestaLibReloaded.Config/FiestaLibReloaded.Config.csproj lib/FiestaLib-Reloaded/src/FiestaLibReloaded.Config/
RUN dotnet restore src/OPTool/OPTool.csproj

# Copy everything and publish
COPY . .
RUN dotnet publish src/OPTool/OPTool.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:5160
EXPOSE 5160

ENTRYPOINT ["dotnet", "OPTool.dll"]
