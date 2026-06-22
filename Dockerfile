FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

ARG PROJECT
COPY . .
RUN dotnet restore CoreWatch.slnx
RUN dotnet publish "$PROJECT" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ARG DLL
ENV DLL=$DLL
COPY --from=build /app/publish .
ENTRYPOINT ["sh", "-c", "dotnet $DLL"]
