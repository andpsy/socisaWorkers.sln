FROM microsoft/dotnet:1.0-runtime

WORKDIR /socisaWorkers

COPY . .


# CMD dotnet run socisaWorkers.dll --host=db_server --port=6603 --user=root --password=scasca --database=socisa
CMD dotnet socisaWorkers.dll