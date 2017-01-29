# FROM microsoft/dotnet:1.0-runtime
# WORKDIR /socisaWorkers
# COPY . .
# COPY /runtimes /runtimes
# CMD dotnet socisaWorkers.dll


FROM microsoft/dotnet:1.0.0-preview2-sdk

WORKDIR /socisaWorkers

ADD src/socisaWorkers /socisaWorkers/src/socisaWorkers

RUN dotnet restore -v minimal src/ \
    && dotnet publish -c Debug -o ./ src/socisaWorkers/ \
    && rm -rf src/ $HOME/.nuget/

CMD dotnet socisaWorkers.dll