# Stage 1: Build the application
FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src

# Copy the project file and restore dependencies.
COPY *.csproj ./
RUN dotnet restore

# Copy the rest of the source code and build the application.
COPY . ./
RUN dotnet publish -c Release -o /app/publish

# Stage 2: Create the runtime image
FROM mcr.microsoft.com/dotnet/runtime:6.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# The application is interactive, so we use an entrypoint.
ENTRYPOINT ["dotnet", "Vault-Transfer.dll"]