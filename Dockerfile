# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src

COPY InvoiceSchedulerJob.sln ./
COPY InvoiceSchedulerJob.csproj ./
RUN dotnet restore ./InvoiceSchedulerJob.csproj

COPY . .
RUN dotnet publish ./InvoiceSchedulerJob.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:7.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

COPY <<'EOF' /app/entrypoint.sh
#!/bin/sh
set -e

# GitHub workflow currently sets ASPNETCORE_ENVIRONMENT.
# This app uses generic host; DOTNET_ENVIRONMENT is the primary variable.
if [ -z "${DOTNET_ENVIRONMENT:-}" ] && [ -n "${ASPNETCORE_ENVIRONMENT:-}" ]; then
  export DOTNET_ENVIRONMENT="$ASPNETCORE_ENVIRONMENT"
fi

exec dotnet InvoiceSchedulerJob.dll "$@"
EOF

RUN chmod +x /app/entrypoint.sh

ENTRYPOINT ["/app/entrypoint.sh"]
