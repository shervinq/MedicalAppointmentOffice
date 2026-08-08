FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY MedicalAppointmentOffice.sln Directory.Build.props ./
COPY src/MedicalAppointmentOffice/MedicalAppointmentOffice.csproj src/MedicalAppointmentOffice/
COPY tests/MedicalAppointmentOffice.Tests/MedicalAppointmentOffice.Tests.csproj tests/MedicalAppointmentOffice.Tests/
RUN dotnet restore MedicalAppointmentOffice.sln

COPY . .
RUN dotnet test MedicalAppointmentOffice.sln -c Release --no-restore
RUN dotnet publish src/MedicalAppointmentOffice/MedicalAppointmentOffice.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && addgroup --system appgroup \
    && adduser --system --ingroup appgroup appuser \
    && mkdir /app/data \
    && chown -R appuser:appgroup /app
COPY --from=build --chown=appuser:appgroup /app/publish .
USER appuser
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "MedicalAppointmentOffice.dll"]
