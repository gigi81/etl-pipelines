#!/bin/sh
set -e

# nuget (bagetter) has no committed healthcheck this compose file can depend_on:
# condition: service_healthy for - its own image's tooling isn't something this repo controls or
# can verify (unlike Server's own image, where curl was explicitly installed for exactly this
# reason). Retrying the push itself, a few times with a short delay, is what actually absorbs
# that startup race instead - a connection refused here is as good a "not up yet" signal as any
# purpose-built probe would be.
max_attempts=30

for package in /packages/*.nupkg; do
    attempts=0
    until dotnet nuget push "$package" \
        --source http://nuget:8080/v3/index.json \
        --api-key any \
        --allow-insecure-connections
    do
        attempts=$((attempts + 1))
        if [ "$attempts" -ge "$max_attempts" ]; then
            echo "Failed to push $package to nuget after $max_attempts attempts" >&2
            exit 1
        fi
        sleep 2
    done
done
