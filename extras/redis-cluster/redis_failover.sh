#!/bin/bash

# Redis Cluster Failover Script
# This script simulates a primary node failure to trigger automatic failover

set -e

# Configuration
REDIS_HOST="172.17.0.1"
REDIS_PORTS=(6379 6380 6381 6382 6383 6384)
CONTAINER_PREFIX="redis-node-"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Function to print colored output
print_status() {
    echo -e "${BLUE}[INFO]${NC} $1"
}

print_success() {
    echo -e "${GREEN}[SUCCESS]${NC} $1"
}

print_warning() {
    echo -e "${YELLOW}[WARNING]${NC} $1"
}

print_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# Function to get cluster info
get_cluster_info() {
    print_status "Current cluster status:"
    redis-cli -h $REDIS_HOST -p 6379 cluster nodes | grep -E "(master|slave)" | sort
    echo
}

# Function to identify primary nodes
get_primary_nodes() {
    redis-cli -h $REDIS_HOST -p 6379 cluster nodes | grep "master" | awk '{print $2}' | cut -d':' -f2 | cut -d'@' -f1
}

# Function to get node info by port
get_node_info() {
    local port=$1
    redis-cli -h $REDIS_HOST -p $port cluster nodes | grep "myself"
}

# Function to stop a container (simulate node failure)
simulate_node_failure() {
    local node_num=$1
    local container_name="${CONTAINER_PREFIX}${node_num}"
    
    print_warning "Simulating failure of $container_name..."
    docker stop $container_name
    print_success "Container $container_name stopped"
}

# Function to start a container (recover node)
recover_node() {
    local node_num=$1
    local container_name="${CONTAINER_PREFIX}${node_num}"
    
    print_status "Recovering $container_name..."
    docker start $container_name
    
    # Wait for node to be ready
    local port=$((6378 + node_num))
    print_status "Waiting for node on port $port to be ready..."
    until redis-cli -h $REDIS_HOST -p $port ping >/dev/null 2>&1; do
        sleep 1
        echo -n "."
    done
    echo
    print_success "Container $container_name recovered"
}

# Function to wait for failover to complete
wait_for_failover() {
    print_status "Waiting for failover to complete..."
    sleep 5
    
    # Check if cluster is still functional
    local max_attempts=10
    local attempt=1
    
    while [ $attempt -le $max_attempts ]; do
        if redis-cli -h $REDIS_HOST -p 6379 cluster info | grep -q "cluster_state:ok"; then
            print_success "Cluster failover completed successfully"
            return 0
        fi
        print_status "Attempt $attempt/$max_attempts - Cluster still recovering..."
        sleep 2
        ((attempt++))
    done
    
    print_error "Cluster may not have recovered properly"
    return 1
}

# Function to test cluster operations
test_cluster_operations() {
    print_status "Testing cluster operations..."
    
    # Test SET operation
    if redis-cli -h $REDIS_HOST -p 6379 -c set test_key "failover_test_$(date)" >/dev/null 2>&1; then
        print_success "SET operation successful"
    else
        print_error "SET operation failed"
        return 1
    fi
    
    # Test GET operation
    if redis-cli -h $REDIS_HOST -p 6379 -c get test_key >/dev/null 2>&1; then
        print_success "GET operation successful"
    else
        print_error "GET operation failed"
        return 1
    fi
    
    print_success "Cluster operations test passed"
}

# Main menu function
show_menu() {
    echo
    echo "=== Redis Cluster Failover Script ==="
    echo "1. Show cluster status"
    echo "2. Simulate failover (stop random primary)"
    echo "3. Simulate failover (choose specific node)"
    echo "4. Recover all stopped nodes"
    echo "5. Full failover test (stop -> wait -> recover)"
    echo "6. Exit"
    echo
}

# Function to perform automatic failover test
perform_failover_test() {
    print_status "=== Starting Automatic Failover Test ==="
    
    # Show initial status
    get_cluster_info
    
    # Get primary nodes
    primary_ports=($(get_primary_nodes))
    if [ ${#primary_ports[@]} -eq 0 ]; then
        print_error "No primary nodes found!"
        return 1
    fi
    
    # Select random primary
    random_primary=${primary_ports[$RANDOM % ${#primary_ports[@]}]}
    node_num=$((random_primary - 6378))
    
    print_status "Selected primary on port $random_primary (node-$node_num) for failover test"
    
    # Simulate failure
    simulate_node_failure $node_num
    
    # Wait for failover
    wait_for_failover
    
    # Show status after failover
    print_status "Cluster status after failover:"
    get_cluster_info
    
    # Test operations
    test_cluster_operations
    
    # Ask if user wants to recover
    echo
    read -p "Do you want to recover the failed node? (y/n): " -n 1 -r
    echo
    if [[ $REPLY =~ ^[Yy]$ ]]; then
        recover_node $node_num
        sleep 3
        print_status "Final cluster status:"
        get_cluster_info
    fi
}

# Function to choose specific node for failover
choose_node_failover() {
    primary_ports=($(get_primary_nodes))
    if [ ${#primary_ports[@]} -eq 0 ]; then
        print_error "No primary nodes found!"
        return 1
    fi
    
    print_status "Available primary nodes:"
    for i in "${!primary_ports[@]}"; do
        local port=${primary_ports[$i]}
        local node_num=$((port - 6378))
        echo "$((i+1)). redis-node-$node_num (port $port)"
    done
    
    echo
    read -p "Choose node to fail (1-${#primary_ports[@]}): " choice
    
    if [[ $choice -ge 1 && $choice -le ${#primary_ports[@]} ]]; then
        local selected_port=${primary_ports[$((choice-1))]}
        local node_num=$((selected_port - 6378))
        
        simulate_node_failure $node_num
        wait_for_failover
        get_cluster_info
        test_cluster_operations
    else
        print_error "Invalid choice"
    fi
}

# Function to recover all stopped nodes
recover_all_nodes() {
    print_status "Checking for stopped Redis containers..."
    
    local recovered=0
    for i in {1..6}; do
        local container_name="${CONTAINER_PREFIX}${i}"
        if ! docker ps --format "table {{.Names}}" | grep -q "^${container_name}$"; then
            if docker ps -a --format "table {{.Names}}" | grep -q "^${container_name}$"; then
                recover_node $i
                ((recovered++))
            fi
        fi
    done
    
    if [ $recovered -eq 0 ]; then
        print_status "No stopped Redis containers found"
    else
        print_success "Recovered $recovered node(s)"
        sleep 3
        get_cluster_info
    fi
}

# Main script execution
main() {
    # Check if redis-cli is available
    if ! command -v redis-cli &> /dev/null; then
        print_error "redis-cli is not installed or not in PATH"
        exit 1
    fi
    
    # Check if Docker is running
    if ! docker ps &> /dev/null; then
        print_error "Docker is not running or not accessible"
        exit 1
    fi
    
    # Main loop
    while true; do
        show_menu
        read -p "Choose an option (1-6): " choice
        
        case $choice in
            1)
                get_cluster_info
                ;;
            2)
                primary_ports=($(get_primary_nodes))
                if [ ${#primary_ports[@]} -eq 0 ]; then
                    print_error "No primary nodes found!"
                else
                    random_primary=${primary_ports[$RANDOM % ${#primary_ports[@]}]}
                    node_num=$((random_primary - 6378))
                    simulate_node_failure $node_num
                    wait_for_failover
                    get_cluster_info
                fi
                ;;
            3)
                choose_node_failover
                ;;
            4)
                recover_all_nodes
                ;;
            5)
                perform_failover_test
                ;;
            6)
                print_success "Exiting..."
                exit 0
                ;;
            *)
                print_error "Invalid option. Please choose 1-6."
                ;;
        esac
        
        echo
        read -p "Press Enter to continue..."
    done
}

# Run main function
main