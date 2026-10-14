FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /source

COPY src/Memrune.Protocol/Memrune.Protocol.csproj src/Memrune.Protocol/
COPY src/Memrune.Core/Memrune.Core.csproj src/Memrune.Core/
COPY src/Memrune.Server/Memrune.Server.csproj src/Memrune.Server/
COPY tools/Memrune.LoadTest/Memrune.LoadTest.csproj tools/Memrune.LoadTest/
RUN dotnet restore src/Memrune.Server/Memrune.Server.csproj \
    && dotnet restore tools/Memrune.LoadTest/Memrune.LoadTest.csproj

COPY src/ src/
COPY tools/Memrune.LoadTest/ tools/Memrune.LoadTest/
RUN dotnet publish src/Memrune.Server/Memrune.Server.csproj -c Release --no-restore -o /publish/server \
    && dotnet publish tools/Memrune.LoadTest/Memrune.LoadTest.csproj -c Release --no-restore -o /publish/benchmark

FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:939555726cae4c916d212323ac3c0792996dcac2614f00e82cc40fcf059e1af3 AS memrune
WORKDIR /app
COPY --from=build /publish/server/ ./
ENTRYPOINT ["dotnet", "Memrune.Server.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:939555726cae4c916d212323ac3c0792996dcac2614f00e82cc40fcf059e1af3 AS benchmark
WORKDIR /app
COPY --from=build /publish/benchmark/ ./
ENTRYPOINT ["dotnet", "Memrune.LoadTest.dll"]