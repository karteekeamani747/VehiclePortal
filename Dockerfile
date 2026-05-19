# ── Stage 1: Build ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["VehiclePortal.csproj", "."]
RUN dotnet restore "VehiclePortal.csproj"

COPY . .
RUN dotnet publish "VehiclePortal.csproj" -c Release -o /app/publish /p:UseAppHost=false

# ── Stage 2: Runtime ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV LD_LIBRARY_PATH=/usr/lib/x86_64-linux-gnu:$LD_LIBRARY_PATH

# Install Tesseract OCR and English language data
RUN apt-get update && apt-get install -y \
    tesseract-ocr \
    tesseract-ocr-eng \
    libtesseract-dev \
    libleptonica-dev \
    && ln -sf /usr/lib/x86_64-linux-gnu/libtesseract.so.5.0.3 /usr/lib/x86_64-linux-gnu/libtesseract.so.5 \
    && ln -sf /usr/lib/x86_64-linux-gnu/libtesseract.so.5 /usr/lib/x86_64-linux-gnu/libtesseract50.so \
    && ln -sf /usr/lib/x86_64-linux-gnu/liblept.so.5.0.4 /usr/lib/x86_64-linux-gnu/liblept.so.5 \
    && ln -sf /usr/lib/x86_64-linux-gnu/liblept.so.5 /usr/lib/x86_64-linux-gnu/liblept1753.so \
    && ln -sf /usr/lib/x86_64-linux-gnu/liblept.so.5.0.4 /usr/lib/x86_64-linux-gnu/libleptonica-1.82.0.so \
    && ldconfig \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /app/data /app/uploads

COPY --from=build /app/publish .

EXPOSE 8080
EXPOSE 8081

ENTRYPOINT ["dotnet", "VehiclePortal.dll"]