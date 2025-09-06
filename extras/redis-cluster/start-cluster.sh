# Clean up completely
docker-compose down
rm -rf data-node-*

# Start the cluster
docker-compose up -d

# Watch the initialization
docker logs -f redis-cluster-init